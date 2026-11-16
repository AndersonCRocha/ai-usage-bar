import Foundation

/// Hooks the app into Claude Code's status line. Claude Code's `settings.json` gets
/// `"statusLine": {"type": "command", "command": "'…/AIUsageBar' --statusline"}`; that command keeps
/// the numbers (see `ClaudeCodeReading`) and then runs the user's own status line, if there was
/// one, so what Claude Code shows does not change. Turning it off puts the user's status line back.
/// Same rules as the Windows and Linux apps.
public struct ClaudeCodeBridge: Sendable {
    public static let flag = "--statusline"

    /// The switch in ⚙ → Claude.
    public static var menuTitle: String {
        tr("Ler uso pelo Claude Code (statusline)", "Read usage from Claude Code (status line)")
    }

    /// Added to the "too many requests" notice while Claude has nothing to show and the switch is off.
    public static var hint: String {
        tr(
            "Usa o Claude Code no terminal? Ative ⚙ → Claude → Ler uso pelo Claude Code para ver os números sem esperar.",
            "Using Claude Code in a terminal? Turn on ⚙ → Claude → Read usage from Claude Code to see the numbers without waiting."
        )
    }

    /// The app's own folder: the numbers and the user's previous status line.
    public var directory: URL
    /// Claude Code's user settings.
    public var settingsURL: URL

    public init(directory: URL, settingsURL: URL) {
        self.directory = directory
        self.settingsURL = settingsURL
    }

    /// `~/Library/Application Support/AIUsageBar` and `~/.claude/settings.json` (or `$CLAUDE_CONFIG_DIR/settings.json`).
    public static var standard: ClaudeCodeBridge {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support")
        return ClaudeCodeBridge(
            directory: support.appendingPathComponent("AIUsageBar"),
            settingsURL: settingsURL(environment: ProcessInfo.processInfo.environment)
        )
    }

    public static func settingsURL(environment: [String: String]) -> URL {
        let base = environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude")
        return base.appendingPathComponent("settings.json")
    }

    var readingURL: URL { directory.appendingPathComponent("claude-code-usage.json") }
    var previousURL: URL { directory.appendingPathComponent("claude-code-statusline.json") }

    // MARK: The status line command

    /// What `--statusline` does: keeps the numbers, then hands the same input to the user's own
    /// status line and returns its exit code (no output and 0 when there is none).
    public func runStatusLine(input: Data, now: Date = Date()) -> Int32 {
        record(statusLine: input, now: now)
        guard let previous = previousCommand() else { return 0 }
        return Self.run(previous, input: input)
    }

    /// Merges the numbers in one status line input into the saved ones.
    public func record(statusLine data: Data, now: Date) {
        guard let reading = ClaudeCodeReading.fromStatusLine(data, now: now, modificationDate: Self.modificationDate),
              !reading.isEmpty
        else { return }
        let old = loadReading()
        let merged = (old ?? ClaudeCodeReading()).merged(with: reading, now: now)
        guard merged != old else { return }
        try? save(merged)
    }

    public func loadReading() -> ClaudeCodeReading? {
        guard let data = try? Data(contentsOf: readingURL) else { return nil }
        return try? JSONDecoder().decode(ClaudeCodeReading.self, from: data)
    }

    /// When the saved numbers last changed, so the app reloads them only then.
    public func readingDate() -> Date? { Self.modificationDate(readingURL.path) }

    func save(_ reading: ClaudeCodeReading) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        // Written to a temporary file and moved: sessions writing at once never leave half a file.
        try encoder.encode(reading).write(to: readingURL, options: .atomic)
    }

    /// The user's own status line command, saved when the app's was put in.
    public func previousCommand() -> String? {
        guard let line = previous()?.object, line["type"] as? String == "command",
              let command = line["command"] as? String,
              !command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !Self.isOurs(command)
        else { return nil }
        return command
    }

    /// The user's `statusLine` from before, as an object and as the exact text it had in the file.
    func previous() -> (object: [String: Any]?, text: String?)? {
        guard let data = try? Data(contentsOf: previousURL),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return (root["previous"] as? [String: Any], root["previous_json"] as? String)
    }

    func savePrevious(object: Any?, text: String?) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let root: [String: Any] = ["previous": object ?? NSNull(), "previous_json": text ?? NSNull()]
        let data = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try data.write(to: previousURL, options: .atomic)
    }

    /// Runs the user's status line the way Claude Code does (through a shell), with the same input.
    static func run(_ command: String, input: Data) -> Int32 {
        // The user's command may exit without reading its input.
        signal(SIGPIPE, SIG_IGN)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = ["-c", command]
        let stdin = Pipe()
        process.standardInput = stdin
        do { try process.run() } catch { return 127 }
        try? stdin.fileHandleForWriting.write(contentsOf: input)
        try? stdin.fileHandleForWriting.close()
        process.waitUntilExit()
        return process.terminationStatus
    }

    static func modificationDate(_ path: String) -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: path))?[.modificationDate] as? Date
    }

    // MARK: Claude Code's settings

    public enum Failure: Error, Equatable {
        /// `settings.json` is not valid JSON (or cannot be read): it is left alone.
        case unreadableSettings
        /// The app runs from a place that goes away: the disk image, or a copy macOS moved aside.
        case unstableLocation
        case write(String)

        public var message: String {
            switch self {
            case .unreadableSettings:
                return tr(
                    "Não consegui ler o settings.json do Claude Code, então não mexi nele. Confira se é um JSON válido.",
                    "Couldn't read Claude Code's settings.json, so it was left alone. Check that it is valid JSON."
                )
            case .unstableLocation:
                return tr(
                    "Arraste o AI Usage Bar para a pasta Aplicativos, abra-o de lá e tente de novo.",
                    "Drag AI Usage Bar to the Applications folder, open it from there and try again."
                )
            case .write(let reason):
                return tr("Não consegui gravar o settings.json do Claude Code: \(reason)", "Couldn't write Claude Code's settings.json: \(reason)")
            }
        }
    }

    /// The app's command, whatever path it was put in with.
    public static func isOurs(_ command: String) -> Bool {
        command.contains("AIUsageBar") && command.contains(flag)
    }

    /// The command Claude Code runs: the app's executable, quoted for the shell.
    public static func command(executable: String) -> String {
        "'" + executable.replacingOccurrences(of: "'", with: "'\\''") + "' " + flag
    }

    /// Not the disk image nor a copy macOS runs from a random folder (App Translocation):
    /// the command would stop working once that place is gone.
    public static func isStableLocation(_ executable: String) -> Bool {
        !executable.contains("/AppTranslocation/") && !executable.hasPrefix("/Volumes/AI Usage Bar")
    }

    /// Whether Claude Code's status line is the app's.
    public func isInstalled() -> Bool {
        guard let (_, settings) = try? readSettings() else { return false }
        return Self.ourCommand(in: settings) != nil
    }

    static func ourCommand(in settings: [String: Any]) -> String? {
        guard let line = settings["statusLine"] as? [String: Any],
              let command = line["command"] as? String, isOurs(command)
        else { return nil }
        return command
    }

    /// Puts the app's command in. The user's status line is kept, to run after it and to put back.
    public func install(executable: String) throws {
        guard Self.isStableLocation(executable) else { throw Failure.unstableLocation }
        let (data, settings) = try readSettings()
        var line = settings["statusLine"] as? [String: Any] ?? [:]
        if Self.ourCommand(in: settings) == nil {
            // Saved first: if writing the settings fails, nothing is lost.
            do {
                try savePrevious(object: settings["statusLine"], text: data.flatMap { JSONText.value(of: "statusLine", in: $0) })
            } catch {
                throw Failure.write(error.localizedDescription)
            }
        }
        // The user's padding and refresh interval stay: their status line still runs.
        line["type"] = "command"
        line["command"] = Self.command(executable: executable)
        try writeSettings(data, statusLine: Self.json(line))
    }

    /// Points the command at where the app is now, when it is in and the app moved.
    public func refresh(executable: String) {
        guard Self.isStableLocation(executable),
              let (data, settings) = try? readSettings(),
              let current = Self.ourCommand(in: settings),
              var line = settings["statusLine"] as? [String: Any]
        else { return }
        let command = Self.command(executable: executable)
        guard current != command else { return }
        line["command"] = command
        try? writeSettings(data, statusLine: Self.json(line))
    }

    /// Puts the user's status line back (or none), only when the current one is the app's, and
    /// forgets the numbers.
    public func uninstall() throws {
        let (data, settings) = try readSettings()
        if Self.ourCommand(in: settings) != nil {
            let saved = previous()
            let text = saved?.text ?? saved?.object.map(Self.json)
            try writeSettings(data, statusLine: text)
        }
        try? FileManager.default.removeItem(at: previousURL)
        try? FileManager.default.removeItem(at: readingURL)
    }

    /// The file and its object; none and an empty object when there is no file yet.
    func readSettings() throws -> (Data?, [String: Any]) {
        guard FileManager.default.fileExists(atPath: settingsURL.path) else { return (nil, [:]) }
        guard let data = try? Data(contentsOf: settingsURL) else { throw Failure.unreadableSettings }
        if data.allSatisfy({ JSONText.isSpace($0) }) { return (nil, [:]) }
        guard let settings = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw Failure.unreadableSettings
        }
        return (data, settings)
    }

    /// Changes only the `statusLine` member, so the rest of the file stays exactly as it was.
    func writeSettings(_ data: Data?, statusLine: String?) throws {
        let updated = JSONText.setting("statusLine", to: statusLine, in: data)
        do {
            try FileManager.default.createDirectory(at: settingsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try updated.write(to: settingsURL, options: .atomic)
        } catch {
            throw Failure.write(error.localizedDescription)
        }
    }

    static func json(_ object: [String: Any]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes]) else { return "{}" }
        return String(decoding: data, as: UTF8.self)
    }
}

/// Edits one top-level member of a JSON object's text and leaves every other byte alone.
/// The text must already be valid JSON (checked by the caller).
enum JSONText {
    struct Member {
        var key: String
        var start: Int
        var value: Range<Int>
    }

    static func isSpace(_ byte: UInt8) -> Bool { byte == 0x20 || byte == 0x0A || byte == 0x0D || byte == 0x09 }

    /// The member's value as it is written in the file.
    static func value(of key: String, in data: Data) -> String? {
        let bytes = [UInt8](data)
        guard let object = members(bytes), let member = object.members.first(where: { $0.key == key }) else { return nil }
        return String(decoding: bytes[member.value], as: UTF8.self)
    }

    /// `data` with the member `key` set to `value` (JSON text): replaced where it is, added at the
    /// end when missing, removed when `value` is nil.
    static func setting(_ key: String, to value: String?, in data: Data?) -> Data {
        let quotedKey = json(string: key)
        guard let data, let object = members([UInt8](data)) else {
            return Data((value.map { "{\n  \(quotedKey): \($0)\n}\n" } ?? "{}\n").utf8)
        }
        var bytes = [UInt8](data)
        let list = object.members
        if let index = list.firstIndex(where: { $0.key == key }) {
            let member = list[index]
            if let value {
                bytes.replaceSubrange(member.value, with: Array(value.utf8))
            } else if index > 0 {
                // From the end of the previous member: its comma goes too.
                bytes.removeSubrange(list[index - 1].value.upperBound..<member.value.upperBound)
            } else if list.count > 1 {
                bytes.removeSubrange(member.start..<list[1].start)
            } else {
                bytes.replaceSubrange((object.open + 1)..<object.close, with: Array("\n".utf8))
            }
        } else if let value {
            let indent = indentation(of: list.first, in: bytes) ?? "  "
            if let last = list.last {
                bytes.insert(contentsOf: Array(",\n\(indent)\(quotedKey): \(value)".utf8), at: last.value.upperBound)
            } else {
                bytes.replaceSubrange((object.open + 1)..<object.close, with: Array("\n\(indent)\(quotedKey): \(value)\n".utf8))
            }
        }
        return Data(bytes)
    }

    /// The spaces before the first key on its line, to add a member the same way.
    private static func indentation(of member: Member?, in bytes: [UInt8]) -> String? {
        guard let member else { return nil }
        var start = member.start
        while start > 0, bytes[start - 1] == 0x20 || bytes[start - 1] == 0x09 { start -= 1 }
        guard start > 0, bytes[start - 1] == 0x0A, start < member.start else { return nil }
        return String(decoding: bytes[start..<member.start], as: UTF8.self)
    }

    private static func json(string: String) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: string, options: [.fragmentsAllowed, .withoutEscapingSlashes]) else {
            return "\"\(string)\""
        }
        return String(decoding: data, as: UTF8.self)
    }

    static func members(_ b: [UInt8]) -> (open: Int, close: Int, members: [Member])? {
        var i = 0
        if b.count >= 3, b[0] == 0xEF, b[1] == 0xBB, b[2] == 0xBF { i = 3 }
        func skipSpace() { while i < b.count, isSpace(b[i]) { i += 1 } }
        skipSpace()
        guard i < b.count, b[i] == UInt8(ascii: "{") else { return nil }
        let open = i
        i += 1
        var list: [Member] = []
        while true {
            skipSpace()
            guard i < b.count else { return nil }
            if b[i] == UInt8(ascii: "}") { return (open, i, list) }
            if b[i] == UInt8(ascii: ",") { i += 1; continue }
            guard b[i] == UInt8(ascii: "\""), let keyEnd = endOfString(b, from: i) else { return nil }
            let start = i
            let key = (try? JSONSerialization.jsonObject(with: Data(b[start..<keyEnd]), options: .fragmentsAllowed)) as? String ?? ""
            i = keyEnd
            skipSpace()
            guard i < b.count, b[i] == UInt8(ascii: ":") else { return nil }
            i += 1
            skipSpace()
            guard let valueEnd = endOfValue(b, from: i) else { return nil }
            list.append(Member(key: key, start: start, value: i..<valueEnd))
            i = valueEnd
        }
    }

    private static func endOfString(_ b: [UInt8], from start: Int) -> Int? {
        var j = start + 1
        while j < b.count {
            if b[j] == UInt8(ascii: "\\") { j += 2; continue }
            if b[j] == UInt8(ascii: "\"") { return j + 1 }
            j += 1
        }
        return nil
    }

    private static func endOfValue(_ b: [UInt8], from start: Int) -> Int? {
        guard start < b.count else { return nil }
        switch b[start] {
        case UInt8(ascii: "\""):
            return endOfString(b, from: start)
        case UInt8(ascii: "{"), UInt8(ascii: "["):
            var depth = 0
            var j = start
            while j < b.count {
                switch b[j] {
                case UInt8(ascii: "\""):
                    guard let end = endOfString(b, from: j) else { return nil }
                    j = end
                    continue
                case UInt8(ascii: "{"), UInt8(ascii: "["):
                    depth += 1
                case UInt8(ascii: "}"), UInt8(ascii: "]"):
                    depth -= 1
                    if depth == 0 { return j + 1 }
                default:
                    break
                }
                j += 1
            }
            return nil
        default:
            var j = start
            while j < b.count, !isSpace(b[j]), b[j] != UInt8(ascii: ","), b[j] != UInt8(ascii: "}"), b[j] != UInt8(ascii: "]") { j += 1 }
            return j
        }
    }
}
