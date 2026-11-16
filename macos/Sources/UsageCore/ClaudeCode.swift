import Foundation

/// The Claude plan limits as Claude Code sees them. Claude Code hands them to its status line
/// command after every model response (`rate_limits` in the JSON on stdin), so reading them costs
/// no request and is never rate limited. Only the session (5 h) and weekly limits, only for
/// Pro, Max and Ultra, and only while Claude Code runs in a terminal. Same rules as the Windows
/// and Linux apps.
public struct ClaudeCodeReading: Equatable, Sendable {
    public struct Window: Equatable, Sendable {
        public var percent: Double
        public var resetsAt: Date
        /// When Claude Code got this number: when its session last wrote its transcript. An idle
        /// session repeats what it saw long ago (after a settings change, for one).
        public var seenAt: Date

        public init(percent: Double, resetsAt: Date, seenAt: Date) {
            self.percent = percent
            self.resetsAt = resetsAt
            self.seenAt = seenAt
        }
    }

    public var session: Window?
    public var weekly: Window?

    public init(session: Window? = nil, weekly: Window? = nil) {
        self.session = session
        self.weekly = weekly
    }

    public var isEmpty: Bool { session == nil && weekly == nil }

    /// Limits at most this far apart reset at the same moment, so they belong to the same period.
    static let samePeriod: TimeInterval = 600

    static func samePeriod(_ a: Date, _ b: Date) -> Bool { abs(a.timeIntervalSince(b)) <= samePeriod }

    /// Without the windows whose reset already passed.
    public func current(now: Date) -> ClaudeCodeReading {
        ClaudeCodeReading(session: session.flatMap { $0.resetsAt > now ? $0 : nil }, weekly: weekly.flatMap { $0.resetsAt > now ? $0 : nil })
    }

    /// Many sessions report, and an idle one repeats an old number. Usage only grows within a
    /// period, so the later period wins and, within one period, the higher number.
    public func merged(with other: ClaudeCodeReading, now: Date) -> ClaudeCodeReading {
        ClaudeCodeReading(session: Self.pick(session, other.session, now: now), weekly: Self.pick(weekly, other.weekly, now: now))
    }

    static func pick(_ a: Window?, _ b: Window?, now: Date) -> Window? {
        let a = a.flatMap { $0.resetsAt > now ? $0 : nil }
        let b = b.flatMap { $0.resetsAt > now ? $0 : nil }
        guard let a else { return b }
        guard let b else { return a }
        if !samePeriod(a.resetsAt, b.resetsAt) { return a.resetsAt > b.resetsAt ? a : b }
        if a.percent != b.percent { return a.percent > b.percent ? a : b }
        return a.seenAt >= b.seenAt ? a : b
    }

    /// The `rate_limits` in the JSON a status line command gets on stdin; nil when there are none
    /// (an API key login, or before the session's first response). `modificationDate` reads the
    /// transcript's date, which says how old the numbers are.
    public static func fromStatusLine(_ data: Data, now: Date, modificationDate: (String) -> Date?) -> ClaudeCodeReading? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let limits = root["rate_limits"] as? [String: Any]
        else { return nil }
        let transcript = (root["transcript_path"] as? String).flatMap(modificationDate)
        let seenAt = min(transcript ?? now, now)
        func window(_ key: String) -> Window? {
            guard let entry = limits[key] as? [String: Any],
                  let percent = (entry["used_percentage"] as? NSNumber)?.doubleValue, percent.isFinite,
                  var resets = (entry["resets_at"] as? NSNumber)?.doubleValue, resets.isFinite, resets > 0
            else { return nil }
            if resets > 100_000_000_000 { resets /= 1000 } // milliseconds
            return Window(percent: max(percent, 0), resetsAt: Date(timeIntervalSince1970: resets), seenAt: seenAt)
        }
        return ClaudeCodeReading(session: window("five_hour"), weekly: window("seven_day"))
    }
}

extension ClaudeCodeReading: Codable {
    enum CodingKeys: String, CodingKey { case session, weekly }
}

extension ClaudeCodeReading.Window: Codable {
    enum CodingKeys: String, CodingKey {
        case percent
        case resetsAt = "resets_at"
        case seenAt = "seen_at"
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        percent = try c.decode(Double.self, forKey: .percent)
        resetsAt = Date(timeIntervalSince1970: try c.decode(Double.self, forKey: .resetsAt))
        seenAt = Date(timeIntervalSince1970: try c.decode(Double.self, forKey: .seenAt))
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(percent, forKey: .percent)
        try c.encode(resetsAt.timeIntervalSince1970, forKey: .resetsAt)
        try c.encode(seenAt.timeIntervalSince1970, forKey: .seenAt)
    }
}

/// Puts Claude Code's numbers together with the usage endpoint's.
public enum ClaudeCodeUsage {
    /// The endpoint's snapshot, with Claude Code's session and weekly numbers where they are newer:
    /// a later period, or a higher number in the same one. With no snapshot (the endpoint refused
    /// from the start), Claude Code's two limits alone. Returns the time of the newest number used.
    public static func combine(
        fetched: ProviderSnapshot?, fetchedAt: Date?, reading: ClaudeCodeReading?, now: Date
    ) -> (snapshot: ProviderSnapshot?, updated: Date?) {
        guard let reading = reading?.current(now: now), !reading.isEmpty else { return (fetched, fetchedAt) }
        let windows: [(kind: String, group: LimitItem.Group, window: ClaudeCodeReading.Window?)] = [
            ("session", .session, reading.session),
            ("weekly_all", .weekly, reading.weekly),
        ]

        guard var snapshot = fetched else {
            let items = windows.compactMap { entry in
                entry.window.map { item(kind: entry.kind, group: entry.group, window: $0) }
            }
            let updated = windows.compactMap(\.window?.seenAt).max()
            return (ProviderSnapshot(provider: .claude, planName: nil, items: items, notes: [.fromClaudeCode]), updated)
        }

        var updated = fetchedAt
        let fetchedTime = fetchedAt ?? .distantPast
        for entry in windows {
            guard let window = entry.window else { continue }
            guard let index = snapshot.items.firstIndex(where: { $0.kind == entry.kind }) else {
                if window.seenAt > fetchedTime {
                    let position = entry.kind == "session" ? 0 : min(1, snapshot.items.count)
                    snapshot.items.insert(item(kind: entry.kind, group: entry.group, window: window), at: position)
                    updated = max(updated ?? window.seenAt, window.seenAt)
                }
                continue
            }
            var shown = snapshot.items[index]
            if let resetsAt = shown.resetsAt, ClaudeCodeReading.samePeriod(resetsAt, window.resetsAt) {
                guard window.percent > shown.percent else { continue }
            } else if let resetsAt = shown.resetsAt, resetsAt > window.resetsAt {
                continue
            } else {
                // A later period than the endpoint's (or one it gave no date for): only if seen after it.
                guard window.seenAt > fetchedTime else { continue }
                shown.resetsAt = window.resetsAt
            }
            shown.percent = window.percent
            shown.severity = Severity(api: nil, percent: window.percent)
            snapshot.items[index] = shown
            updated = max(updated ?? window.seenAt, window.seenAt)
        }
        return (snapshot, updated)
    }

    private static func item(kind: String, group: LimitItem.Group, window: ClaudeCodeReading.Window) -> LimitItem {
        LimitItem(
            id: "claude-code-\(kind)", kind: kind, percent: window.percent,
            severity: Severity(api: nil, percent: window.percent), resetsAt: window.resetsAt, group: group
        )
    }
}
