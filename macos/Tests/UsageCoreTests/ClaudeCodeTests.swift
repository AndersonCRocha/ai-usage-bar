import XCTest
@testable import UsageCore

final class ClaudeCodeTests: XCTestCase {
    /// 2027-01-15 08:00:00 UTC.
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private var sessionReset: Date { now.addingTimeInterval(3 * 3600) }
    private var weekReset: Date { now.addingTimeInterval(5 * 86_400) }
    private var folder: URL!

    override func setUpWithError() throws {
        AppLanguage.current = .portuguese
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("ClaudeCodeTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: folder)
    }

    private func statusLine(session: Double? = 4, weekly: Double? = 19, transcript: String? = "/tmp/session.jsonl") -> Data {
        var limits: [String: Any] = [:]
        if let session { limits["five_hour"] = ["used_percentage": session, "resets_at": sessionReset.timeIntervalSince1970] }
        if let weekly { limits["seven_day"] = ["used_percentage": weekly, "resets_at": weekReset.timeIntervalSince1970] }
        var root: [String: Any] = ["session_id": "abc", "model": ["id": "claude-opus-5-5"], "rate_limits": limits]
        if let transcript { root["transcript_path"] = transcript }
        return try! JSONSerialization.data(withJSONObject: root)
    }

    private func window(_ percent: Double, resets: Date, seen: Date? = nil) -> ClaudeCodeReading.Window {
        .init(percent: percent, resetsAt: resets, seenAt: seen ?? now)
    }

    // MARK: Reading the status line input

    func testReadsTheRateLimitsAndDatesThemByTheTranscript() {
        let seen = now.addingTimeInterval(-600)
        let reading = ClaudeCodeReading.fromStatusLine(statusLine(), now: now) { path in
            path == "/tmp/session.jsonl" ? seen : nil
        }
        XCTAssertEqual(reading?.session, window(4, resets: sessionReset, seen: seen))
        XCTAssertEqual(reading?.weekly, window(19, resets: weekReset, seen: seen))

        let undated = ClaudeCodeReading.fromStatusLine(statusLine(transcript: nil), now: now) { _ in nil }
        XCTAssertEqual(undated?.session?.seenAt, now, "no transcript: seen now")
        let future = ClaudeCodeReading.fromStatusLine(statusLine(), now: now) { _ in self.now.addingTimeInterval(60) }
        XCTAssertEqual(future?.session?.seenAt, now, "never later than now")
    }

    func testMissingLimits() {
        let onlyWeek = ClaudeCodeReading.fromStatusLine(statusLine(session: nil), now: now) { _ in nil }
        XCTAssertNil(onlyWeek?.session)
        XCTAssertEqual(onlyWeek?.weekly?.percent, 19)
        let apiKey = try! JSONSerialization.data(withJSONObject: ["session_id": "abc", "rate_limits": NSNull()])
        XCTAssertNil(ClaudeCodeReading.fromStatusLine(apiKey, now: now) { _ in nil })
        XCTAssertNil(ClaudeCodeReading.fromStatusLine(Data("not json".utf8), now: now) { _ in nil })
    }

    // MARK: Many sessions

    func testLaterPeriodWinsThenTheHigherNumber() {
        let old = ClaudeCodeReading(session: window(80, resets: sessionReset.addingTimeInterval(-5 * 3600)), weekly: window(13, resets: weekReset.addingTimeInterval(-3 * 86_400)))
        let fresh = ClaudeCodeReading(session: window(4, resets: sessionReset), weekly: window(19, resets: weekReset))
        let idle = ClaudeCodeReading(session: window(1, resets: sessionReset), weekly: window(14, resets: weekReset))

        // The old session period already ended; the old week resets earlier, so the newer week wins.
        XCTAssertEqual(old.merged(with: fresh, now: now), fresh)
        XCTAssertEqual(fresh.merged(with: idle, now: now), fresh, "an idle session's lower number does not win")
        XCTAssertEqual(idle.merged(with: fresh, now: now), fresh)

        let sameLater = ClaudeCodeReading(session: window(4, resets: sessionReset.addingTimeInterval(1), seen: now.addingTimeInterval(30)))
        XCTAssertEqual(fresh.merged(with: sameLater, now: now).session?.seenAt, now.addingTimeInterval(30), "same number seen later")
        XCTAssertEqual(fresh.merged(with: ClaudeCodeReading(), now: now), fresh, "a missing window keeps the saved one")
        XCTAssertNil(fresh.merged(with: ClaudeCodeReading(), now: sessionReset).session, "until its reset passes")
    }

    // MARK: Putting it together with the endpoint

    private func fetched(session: Double = 5, weekly: Double = 19) -> ProviderSnapshot {
        ProviderSnapshot(
            provider: .claude, planName: "Max 5x",
            items: [
                LimitItem(id: "0-session-", kind: "session", percent: session, severity: .normal, resetsAt: sessionReset, group: .session),
                LimitItem(id: "1-weekly_all-", kind: "weekly_all", percent: weekly, severity: .normal, resetsAt: weekReset, group: .weekly),
                LimitItem(id: "2-weekly_scoped-Fable", kind: "weekly_scoped", scopeName: "Fable", percent: 0, severity: .normal, resetsAt: weekReset, group: .weekly),
            ],
            breakdown: [BreakdownItem(id: "claude_code", displayName: "Claude Code", percent: 95)]
        )
    }

    func testNothingFromTheEndpoint() {
        let seen = now.addingTimeInterval(-120)
        let reading = ClaudeCodeReading(session: window(4, resets: sessionReset, seen: seen), weekly: window(19, resets: weekReset, seen: seen))
        let (snapshot, updated) = ClaudeCodeUsage.combine(fetched: nil, fetchedAt: nil, reading: reading, now: now)
        XCTAssertEqual(snapshot?.items.map(\.kind), ["session", "weekly_all"])
        XCTAssertEqual(snapshot?.items.map(\.percent), [4, 19])
        XCTAssertEqual(snapshot?.items.map(\.group), [.session, .weekly])
        XCTAssertEqual(snapshot?.notes, [.fromClaudeCode])
        XCTAssertNil(snapshot?.planName)
        XCTAssertEqual(updated, seen)
        XCTAssertEqual(SnapshotNote.fromClaudeCode.text, "Números do Claude Code no terminal. Os outros limites voltam quando o servidor responder.")

        XCTAssertNil(ClaudeCodeUsage.combine(fetched: nil, fetchedAt: nil, reading: reading, now: weekReset).snapshot, "every limit already reset")
    }

    func testRaisesTheEndpointsNumbersInTheSamePeriod() {
        let fetchedAt = now.addingTimeInterval(-900)
        let reading = ClaudeCodeReading(session: window(31, resets: sessionReset.addingTimeInterval(40)), weekly: window(12, resets: weekReset))
        let (snapshot, updated) = ClaudeCodeUsage.combine(fetched: fetched(), fetchedAt: fetchedAt, reading: reading, now: now)
        XCTAssertEqual(snapshot?.items.map(\.percent), [31, 19, 0], "the session goes up; a lower weekly number does not bring it down")
        XCTAssertEqual(snapshot?.items.first?.resetsAt, sessionReset, "the endpoint's reset stays")
        XCTAssertEqual(snapshot?.items.first?.id, "0-session-")
        XCTAssertEqual(snapshot?.breakdown.count, 1)
        XCTAssertEqual(snapshot?.planName, "Max 5x")
        XCTAssertEqual(updated, now)

        let lower = ClaudeCodeReading(session: window(4, resets: sessionReset))
        let unchanged = ClaudeCodeUsage.combine(fetched: fetched(), fetchedAt: fetchedAt, reading: lower, now: now)
        XCTAssertEqual(unchanged.snapshot, fetched())
        XCTAssertEqual(unchanged.updated, fetchedAt)
    }

    func testANewPeriodCountsOnlyWhenSeenAfterTheEndpoint() {
        let fetchedAt = now.addingTimeInterval(-6 * 3600)
        let nextSession = sessionReset.addingTimeInterval(5 * 3600)
        let newer = ClaudeCodeReading(session: window(2, resets: nextSession))
        let snapshot = ClaudeCodeUsage.combine(fetched: fetched(session: 90), fetchedAt: fetchedAt, reading: newer, now: now).snapshot
        XCTAssertEqual(snapshot?.items.first?.percent, 2)
        XCTAssertEqual(snapshot?.items.first?.resetsAt, nextSession)
        XCTAssertEqual(snapshot?.items.first?.severity, .normal)

        let older = ClaudeCodeReading(session: window(2, resets: nextSession, seen: fetchedAt.addingTimeInterval(-60)))
        XCTAssertEqual(ClaudeCodeUsage.combine(fetched: fetched(session: 90), fetchedAt: fetchedAt, reading: older, now: now).snapshot, fetched(session: 90))

        let earlier = ClaudeCodeReading(session: window(70, resets: sessionReset.addingTimeInterval(-3600)))
        XCTAssertEqual(ClaudeCodeUsage.combine(fetched: fetched(), fetchedAt: fetchedAt, reading: earlier, now: now).snapshot, fetched(), "an earlier period never wins")
    }

    // MARK: Editing settings.json

    func testEditsOnlyTheStatusLine() {
        let text = """
        {
          "model": "opus",
          "statusLine": {
            "type": "command",
            "command": "~/.claude/line.sh"
          },
          "theme": "dark"
        }

        """
        let data = Data(text.utf8)
        XCTAssertEqual(JSONText.value(of: "statusLine", in: data), "{\n    \"type\": \"command\",\n    \"command\": \"~/.claude/line.sh\"\n  }")

        let replaced = String(decoding: JSONText.setting("statusLine", to: "{\"x\":1}", in: data), as: UTF8.self)
        XCTAssertEqual(replaced, "{\n  \"model\": \"opus\",\n  \"statusLine\": {\"x\":1},\n  \"theme\": \"dark\"\n}\n")

        let removed = String(decoding: JSONText.setting("statusLine", to: nil, in: data), as: UTF8.self)
        XCTAssertEqual(removed, "{\n  \"model\": \"opus\",\n  \"theme\": \"dark\"\n}\n")
        let removedFirst = String(decoding: JSONText.setting("model", to: nil, in: Data(replaced.utf8)), as: UTF8.self)
        XCTAssertEqual(removedFirst, "{\n  \"statusLine\": {\"x\":1},\n  \"theme\": \"dark\"\n}\n")
        XCTAssertEqual(String(decoding: JSONText.setting("a", to: nil, in: Data("{\"a\": [1, {\"b\": \"}\"}]}".utf8)), as: UTF8.self), "{\n}")

        let added = String(decoding: JSONText.setting("statusLine", to: "{}", in: Data("{\n    \"model\": \"opus\"\n}".utf8)), as: UTF8.self)
        XCTAssertEqual(added, "{\n    \"model\": \"opus\",\n    \"statusLine\": {}\n}")
        XCTAssertEqual(String(decoding: JSONText.setting("statusLine", to: "{}", in: Data("{}".utf8)), as: UTF8.self), "{\n  \"statusLine\": {}\n}")
        XCTAssertEqual(String(decoding: JSONText.setting("statusLine", to: "{}", in: nil), as: UTF8.self), "{\n  \"statusLine\": {}\n}\n")
    }

    private func bridge() -> ClaudeCodeBridge {
        ClaudeCodeBridge(directory: folder.appendingPathComponent("app"), settingsURL: folder.appendingPathComponent("claude/settings.json"))
    }

    private let executable = "/Applications/AI Usage Bar.app/Contents/MacOS/AIUsageBar"

    private func settingsText(_ bridge: ClaudeCodeBridge) -> String? {
        (try? Data(contentsOf: bridge.settingsURL)).map { String(decoding: $0, as: UTF8.self) }
    }

    func testInstallsAndPutsTheUsersStatusLineBack() throws {
        let bridge = bridge()
        try FileManager.default.createDirectory(at: bridge.settingsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let original = "{\n  \"model\": \"opus\",\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"~/.claude/line.sh\",\n    \"padding\": 2\n  }\n}\n"
        try Data(original.utf8).write(to: bridge.settingsURL)

        XCTAssertFalse(bridge.isInstalled())
        try bridge.install(executable: executable)
        XCTAssertTrue(bridge.isInstalled())
        XCTAssertEqual(bridge.previousCommand(), "~/.claude/line.sh")
        let settings = try JSONSerialization.jsonObject(with: Data(contentsOf: bridge.settingsURL)) as! [String: Any]
        let line = settings["statusLine"] as! [String: Any]
        XCTAssertEqual(line["command"] as? String, "'/Applications/AI Usage Bar.app/Contents/MacOS/AIUsageBar' --statusline")
        XCTAssertEqual(line["padding"] as? Int, 2, "the user's padding stays")
        XCTAssertEqual(settings["model"] as? String, "opus")

        // Again (or from another place): still the user's line behind it.
        try bridge.install(executable: executable)
        bridge.refresh(executable: "/Users/me/Applications/AI Usage Bar.app/Contents/MacOS/AIUsageBar")
        XCTAssertTrue(settingsText(bridge)?.contains("/Users/me/Applications/") ?? false)
        XCTAssertEqual(bridge.previousCommand(), "~/.claude/line.sh")

        try bridge.uninstall()
        XCTAssertEqual(settingsText(bridge), original, "byte for byte")
        XCTAssertFalse(bridge.isInstalled())
    }

    func testInstallsWhereThereWasNothing() throws {
        let bridge = bridge()
        try bridge.install(executable: executable)
        XCTAssertNil(bridge.previousCommand())
        XCTAssertTrue(bridge.isInstalled())
        try bridge.uninstall()
        XCTAssertEqual(settingsText(bridge), "{\n}\n")
        bridge.refresh(executable: executable)
        XCTAssertFalse(bridge.isInstalled(), "refresh never puts it in")
    }

    func testLeavesBrokenSettingsAlone() throws {
        let bridge = bridge()
        try FileManager.default.createDirectory(at: bridge.settingsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("{ \"model\": ".utf8).write(to: bridge.settingsURL)
        XCTAssertThrowsError(try bridge.install(executable: executable)) { XCTAssertEqual($0 as? ClaudeCodeBridge.Failure, .unreadableSettings) }
        XCTAssertEqual(settingsText(bridge), "{ \"model\": ")
        XCTAssertThrowsError(try bridge.install(executable: "/private/var/folders/x/AppTranslocation/ABC/d/AI Usage Bar.app/Contents/MacOS/AIUsageBar")) {
            XCTAssertEqual($0 as? ClaudeCodeBridge.Failure, .unstableLocation)
        }
        XCTAssertFalse(ClaudeCodeBridge.isStableLocation("/Volumes/AI Usage Bar/AI Usage Bar.app/Contents/MacOS/AIUsageBar"))
        XCTAssertEqual(ClaudeCodeBridge.command(executable: "/a/it's/AIUsageBar"), "'/a/it'\\''s/AIUsageBar' --statusline")
    }

    func testTheCommandSavesTheNumbersAndRunsTheUsersLine() throws {
        let bridge = bridge()
        let output = folder.appendingPathComponent("seen.json")
        try FileManager.default.createDirectory(at: bridge.settingsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let line = "cat > '\(output.path)'; exit 3"
        let settings = try JSONSerialization.data(withJSONObject: ["statusLine": ["type": "command", "command": line]])
        try settings.write(to: bridge.settingsURL)
        try bridge.install(executable: executable)

        let input = statusLine(transcript: nil)
        XCTAssertEqual(bridge.runStatusLine(input: input, now: now), 3, "the user's exit code")
        XCTAssertEqual(try Data(contentsOf: output), input, "the user's line gets the same input")
        XCTAssertEqual(bridge.loadReading()?.session?.percent, 4)
        XCTAssertEqual(bridge.loadReading()?.weekly?.percent, 19)

        // An idle session repeating a lower number does not overwrite it.
        _ = bridge.runStatusLine(input: statusLine(session: 1, weekly: nil, transcript: nil), now: now)
        XCTAssertEqual(bridge.loadReading()?.session?.percent, 4)
        XCTAssertEqual(bridge.loadReading()?.weekly?.percent, 19)

        try bridge.uninstall()
        XCTAssertNil(bridge.loadReading(), "turning it off forgets the numbers")
        XCTAssertEqual(bridge.runStatusLine(input: input, now: now), 0, "nothing to run")
    }
}
