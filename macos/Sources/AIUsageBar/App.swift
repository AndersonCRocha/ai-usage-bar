import Foundation
import SwiftUI
import UsageCore

/// Claude Code runs `AIUsageBar --statusline` after every response (see `ClaudeCodeBridge`): that
/// is handled here, before AppKit starts, so it stays quick and never adds a second menu bar item.
///
///     AIUsageBar --statusline              (run by Claude Code, with its status line JSON on stdin)
///     AIUsageBar --install-statusline      (the same as ⚙ → Claude → Read usage from Claude Code)
///     AIUsageBar --uninstall-statusline
@main
enum Main {
    static func main() {
        let arguments = CommandLine.arguments
        if arguments.contains(ClaudeCodeBridge.flag) {
            let input = FileHandle.standardInput.readDataToEndOfFile()
            exit(ClaudeCodeBridge.standard.runStatusLine(input: input))
        }
        if arguments.contains("--install-statusline") || arguments.contains("--uninstall-statusline") {
            exit(setUpStatusLine(install: arguments.contains("--install-statusline")))
        }
        AIUsageBarApp.main()
    }

    private static func setUpStatusLine(install: Bool) -> Int32 {
        let bridge = ClaudeCodeBridge.standard
        do {
            if install {
                guard let executable = Bundle.main.executablePath else { return 1 }
                try bridge.install(executable: executable)
                print("Claude Code's status line now runs AI Usage Bar: \(bridge.settingsURL.path)")
            } else {
                try bridge.uninstall()
                print("Claude Code's status line is back to what it was: \(bridge.settingsURL.path)")
            }
            return 0
        } catch {
            let message = (error as? ClaudeCodeBridge.Failure)?.message ?? error.localizedDescription
            FileHandle.standardError.write(Data((message + "\n").utf8))
            return 1
        }
    }
}

struct AIUsageBarApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    // The UI lives in the status item managed by AppDelegate; SwiftUI just
    // needs a scene. LSUIElement in Info.plist keeps the app out of the Dock.
    var body: some Scene {
        Settings { EmptyView() }
    }
}
