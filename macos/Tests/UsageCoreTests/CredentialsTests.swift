import XCTest
@testable import UsageCore

/// How the Claude Code login is looked up. The app has to ask for the same
/// Keychain item Claude Code wrote, and must not settle on a login that
/// expired months ago.
final class CredentialsTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUp() {
        super.setUp()
        AppLanguage.current = .portuguese
    }

    private func login(expiresAt: Date?, token: String = "t") -> Data {
        var oauth: [String: Any] = ["accessToken": token, "subscriptionType": "max"]
        if let expiresAt { oauth["expiresAt"] = expiresAt.timeIntervalSince1970 * 1000 }
        return try! JSONSerialization.data(withJSONObject: ["claudeAiOauth": oauth])
    }

    // MARK: account

    func testAccountIsTheUserName() {
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": "anderson"]), "anderson")
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": "ana.b_c-1"]), "ana.b_c-1")
    }

    func testAccountFallsBackWhenTheUserNameIsUnusable() {
        let fallback = CredentialsStore.fallbackAccount
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": ""]), fallback)
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": "andré"]), fallback, "Claude Code rejects non-ASCII")
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": "two words"]), fallback)
    }

    // MARK: service

    func testServiceHasNoSuffixByDefault() {
        XCTAssertEqual(CredentialsStore.service(environment: [:]), "Claude Code-credentials")
        XCTAssertEqual(CredentialsStore.service(environment: ["CLAUDE_CONFIG_DIR": ""]), "Claude Code-credentials")
    }

    func testCustomConfigDirectoryAddsItsHash() {
        XCTAssertEqual(
            CredentialsStore.service(environment: ["CLAUDE_CONFIG_DIR": "/Users/someone/.config/claude"]),
            "Claude Code-credentials-f6ab3f41",
            "the first eight hex digits of the SHA-256 of the directory"
        )
    }

    func testSecureStorageDirectoryWins() {
        let environment = [
            "CLAUDE_CONFIG_DIR": "/Users/someone/.config/claude",
            "CLAUDE_SECURESTORAGE_CONFIG_DIR": "/tmp/cfg",
        ]
        XCTAssertEqual(CredentialsStore.service(environment: environment), "Claude Code-credentials-519e587f")
        XCTAssertEqual(
            CredentialsStore.service(environment: ["CLAUDE_CONFIG_DIR": "/tmp/cfg", "CLAUDE_SECURESTORAGE_CONFIG_DIR": ""]),
            "Claude Code-credentials",
            "set but empty means the default name, as in Claude Code"
        )
    }

    // MARK: config directory

    func testConfigDirectory() {
        let home = URL(fileURLWithPath: "/Users/someone")
        XCTAssertEqual(
            CredentialsStore.configDirectory(environment: [:], home: home).path,
            "/Users/someone/.claude"
        )
        XCTAssertEqual(
            CredentialsStore.configDirectory(environment: ["CLAUDE_CONFIG_DIR": "/tmp/cfg"], home: home).path,
            "/tmp/cfg"
        )
        XCTAssertEqual(
            CredentialsStore.configDirectory(
                environment: ["CLAUDE_CONFIG_DIR": "/tmp/cfg", "CLAUDE_SECURESTORAGE_CONFIG_DIR": "/tmp/secure"],
                home: home
            ).path,
            "/tmp/secure"
        )
        XCTAssertEqual(
            CredentialsStore.configDirectory(environment: ["CLAUDE_SECURESTORAGE_CONFIG_DIR": ""], home: home).path,
            "/Users/someone/.claude"
        )
    }

    // MARK: choosing between sources

    func testAValidLoginWinsOverAnExpiredOne() throws {
        let stale = login(expiresAt: now.addingTimeInterval(-86_400 * 180), token: "stale")
        let live = login(expiresAt: now.addingTimeInterval(3600), token: "live")
        let chosen = try CredentialsStore.select(from: [{ stale }, { live }], now: now)
        XCTAssertEqual(chosen.accessToken, "live", "a leftover item must not shadow the live login")
    }

    func testAnExpiredLoginIsReturnedWhenNothingElseIsValid() throws {
        let stale = login(expiresAt: now.addingTimeInterval(-60), token: "stale")
        let chosen = try CredentialsStore.select(from: [{ stale }, { nil }], now: now)
        XCTAssertEqual(chosen.accessToken, "stale", "so the user still gets the \"login expired\" notice")
        XCTAssertTrue(chosen.isExpired(now: now))
    }

    func testALoginWithoutAnExpiryCounts() throws {
        let chosen = try CredentialsStore.select(from: [{ self.login(expiresAt: nil) }], now: now)
        XCTAssertFalse(chosen.isExpired(now: now))
    }

    func testHexEncodedSourceIsParsed() throws {
        let hex = login(expiresAt: now.addingTimeInterval(3600), token: "live")
            .map { String(format: "%02x", $0) }.joined()
        let chosen = try CredentialsStore.select(from: [{ Data(hex.utf8) }], now: now)
        XCTAssertEqual(chosen.accessToken, "live")
    }

    func testNoSourceAtAllIsNotFound() {
        XCTAssertThrowsError(try CredentialsStore.select(from: [{ nil }, { nil }], now: now)) { error in
            XCTAssertEqual(error as? CredentialsError, .notFound)
        }
    }

    func testUnreadableDataIsMalformed() {
        XCTAssertThrowsError(try CredentialsStore.select(from: [{ Data("not json".utf8) }], now: now)) { error in
            XCTAssertEqual(error as? CredentialsError, .malformed)
        }
    }

    func testAMalformedSourceDoesNotHideALaterValidOne() throws {
        let live = login(expiresAt: now.addingTimeInterval(3600), token: "live")
        let chosen = try CredentialsStore.select(from: [{ Data("not json".utf8) }, { live }], now: now)
        XCTAssertEqual(chosen.accessToken, "live")
    }
}
