import XCTest
@testable import UsageCore

final class ServiceStatusTests: XCTestCase {
    override func setUp() {
        AppLanguage.current = .english
    }

    private let claude = ProviderID.claude.statusPage!
    private let codex = ProviderID.codex.statusPage!
    private let gemini = ProviderID.gemini.statusPage!

    private func data(_ json: String) -> Data { Data(json.utf8) }

    /// status.claude.com on 2026-11-16, trimmed: only claude.ai had an incident.
    private let claudeAiDegraded = """
    {
      "page": {"url": "https://status.claude.com"},
      "status": {"indicator": "minor", "description": "Partially Degraded Service"},
      "components": [
        {"id": "rwppv331jlwc", "name": "claude.ai", "status": "degraded_performance", "group": false},
        {"id": "k8w3r06qmzrp", "name": "Claude API (api.anthropic.com)", "status": "operational", "group": false},
        {"id": "yyzkbfz2thpt", "name": "Claude Code", "status": "operational", "group": false},
        {"id": "0scnb50nvy53", "name": "Claude for Government", "status": "major_outage", "group": false},
        {"id": "60k546tlxqz7", "name": "Claude on Cloud Platforms", "status": "partial_outage", "group": true}
      ],
      "incidents": [
        {"name": "Claude unable to join meetings", "status": "investigating", "impact": "major",
         "components": [{"name": "claude.ai", "status": "degraded_performance"}]},
        {"name": "Errors for Claude for Government", "status": "identified", "impact": "critical",
         "components": [{"name": "Claude for Government", "status": "major_outage"}]}
      ]
    }
    """

    func testClaudeAiProblemsDoNotCount() throws {
        let status = try ServiceStatus.statuspage(summary: data(claudeAiDegraded), page: claude)
        XCTAssertEqual(status.level, .operational)
        XCTAssertEqual(status.components, [])
        XCTAssertEqual(status.incidents, [])
        XCTAssertFalse(status.isNotice)
    }

    func testClaudeIncidentOnAPartThatMatters() throws {
        let json = """
        {"status": {"indicator": "major"},
         "components": [
           {"name": "claude.ai", "status": "degraded_performance"},
           {"name": "Claude API (api.anthropic.com)", "status": "operational"},
           {"name": "Claude Code", "status": "degraded_performance"}
         ],
         "incidents": [
           {"name": "Claude unable to join meetings", "status": "investigating", "impact": "major",
            "components": [{"name": "claude.ai"}]},
           {"name": "Elevated errors on Claude Code", "status": "identified", "impact": "major",
            "components": [{"name": "Claude Code"}, {"name": "claude.ai"}]}
         ]}
        """
        let status = try ServiceStatus.statuspage(summary: data(json), page: claude)
        // The incident's "major" impact outranks the component's "degraded".
        XCTAssertEqual(status.level, .partialOutage)
        XCTAssertEqual(status.components, ["Claude Code"])
        XCTAssertEqual(status.incidents, ["Elevated errors on Claude Code"])
        XCTAssertTrue(status.isProblem)
        XCTAssertEqual(status.severity, .warning)
        XCTAssertEqual(status.title(for: .claude), "Claude is having issues")
        XCTAssertEqual(status.detailLines, ["Elevated errors on Claude Code", "Affected: Claude Code"])
        XCTAssertEqual(status.pageURL, URL(string: "https://status.claude.com"))
    }

    func testOperational() throws {
        let json = """
        {"status": {"indicator": "none"},
         "components": [{"name": "Claude Code", "status": "operational"}, {"name": "claude.ai", "status": "operational"}],
         "incidents": []}
        """
        let status = try ServiceStatus.statuspage(summary: data(json), page: claude)
        XCTAssertEqual(status.level, .operational)
        XCTAssertFalse(status.isNotice)
        XCTAssertEqual(status.detailLines, [])
    }

    func testUnrelatedOutageIsIgnored() throws {
        let json = """
        {"status": {"indicator": "major"},
         "components": [{"name": "Claude Code", "status": "operational"}, {"name": "Claude Cowork", "status": "major_outage"}],
         "incidents": [{"name": "Cowork down", "status": "investigating", "impact": "critical",
                        "components": [{"name": "Claude Cowork"}]}]}
        """
        let status = try ServiceStatus.statuspage(summary: data(json), page: claude)
        XCTAssertEqual(status.level, .operational)
        XCTAssertEqual(status.incidents, [])
    }

    func testResolvedIncidentIsIgnored() throws {
        let json = """
        {"components": [{"name": "Claude Code", "status": "operational"}],
         "incidents": [{"name": "Old", "status": "resolved", "impact": "major", "components": [{"name": "Claude Code"}]}]}
        """
        XCTAssertEqual(try ServiceStatus.statuspage(summary: data(json), page: claude).level, .operational)
    }

    func testMajorOutageIsCritical() throws {
        let json = """
        {"components": [{"name": "Claude Code", "status": "major_outage"}], "incidents": []}
        """
        let status = try ServiceStatus.statuspage(summary: data(json), page: claude)
        XCTAssertEqual(status.level, .majorOutage)
        XCTAssertEqual(status.severity, .critical)
        XCTAssertEqual(status.title(for: .claude), "Claude is down")
        XCTAssertEqual(status.detailLines, ["Affected: Claude Code"])
    }

    func testMaintenanceIsANoticeOnly() throws {
        let json = """
        {"components": [{"name": "Claude API (api.anthropic.com)", "status": "under_maintenance"}], "incidents": []}
        """
        let status = try ServiceStatus.statuspage(summary: data(json), page: claude)
        XCTAssertEqual(status.level, .maintenance)
        XCTAssertTrue(status.isNotice)
        XCTAssertFalse(status.isProblem)
        XCTAssertEqual(status.components, ["Claude API (api.anthropic.com)"])
    }

    /// incident.io's summary stops at 25 components, before Codex's; its components.json has them all.
    func testCodexComponentsFromTheFullList() throws {
        let summary = """
        {"status": {"indicator": "minor"},
         "components": [{"name": "Chat Completions", "status": "operational"}],
         "incidents": [{"name": "Codex CLI errors", "status": "investigating", "impact": "minor"}]}
        """
        let components = """
        {"components": [
          {"name": "Chat Completions", "status": "operational"},
          {"name": "Codex Web", "status": "operational"},
          {"name": "CLI", "status": "partial_outage"},
          {"name": "VS Code extension", "status": "degraded_performance"}
        ]}
        """
        let status = try ServiceStatus.statuspage(summary: data(summary), components: data(components), page: codex)
        XCTAssertEqual(status.level, .partialOutage)
        XCTAssertEqual(status.components, ["CLI", "VS Code extension"])
        // incident.io's incidents do not say which components they touch.
        XCTAssertEqual(status.incidents, [])
    }

    func testRenamedComponentsFallBackToThePage() throws {
        let json = """
        {"status": {"indicator": "major"},
         "components": [{"name": "Something new", "status": "partial_outage"}, {"name": "Other", "status": "operational"}],
         "incidents": [{"name": "Errors everywhere", "status": "identified", "impact": "major"}]}
        """
        let status = try ServiceStatus.statuspage(summary: data(json), page: claude)
        XCTAssertEqual(status.level, .partialOutage)
        XCTAssertEqual(status.components, ["Something new"])
        XCTAssertEqual(status.incidents, ["Errors everywhere"])
    }

    func testLenientAboutOddElements() throws {
        let json = """
        {"components": [42, {"name": "Claude Code", "status": "degraded_performance"}, {"status": "operational"}],
         "incidents": "nope"}
        """
        let status = try ServiceStatus.statuspage(summary: data(json), page: claude)
        XCTAssertEqual(status.level, .degraded)
        XCTAssertEqual(status.components, ["Claude Code"])
    }

    func testNotJSON() {
        XCTAssertThrowsError(try ServiceStatus.statuspage(summary: data("<html>"), page: claude))
        XCTAssertThrowsError(try ServiceStatus.googleCloud(incidents: data("<html>"), page: gemini))
    }

    func testGoogleCloudOpenIncidentOnGeminiCodeAssist() throws {
        let json = """
        [
          {"external_desc": "Gemini Code Assist users see elevated errors.", "status_impact": "SERVICE_DISRUPTION",
           "begin": "2026-11-16T10:00:00+00:00",
           "affected_products": [{"title": "Gemini Code Assist", "id": "deUeOEPYanfJ9w8cpyBJ"}, {"title": "Cloud Run", "id": "x"}]},
          {"external_desc": "Older Gemini incident", "status_impact": "SERVICE_OUTAGE",
           "begin": "2026-11-01T10:00:00+00:00", "end": "2026-11-01T12:00:00+00:00",
           "affected_products": [{"title": "Gemini Code Assist", "id": "deUeOEPYanfJ9w8cpyBJ"}]},
          {"external_desc": "Storage is slow", "status_impact": "SERVICE_OUTAGE",
           "begin": "2026-11-16T10:00:00+00:00",
           "affected_products": [{"title": "Google Cloud Storage", "id": "y"}]}
        ]
        """
        let status = try ServiceStatus.googleCloud(incidents: data(json), page: gemini)
        XCTAssertEqual(status.level, .partialOutage)
        XCTAssertEqual(status.components, ["Gemini Code Assist"])
        XCTAssertEqual(status.incidents, ["Gemini Code Assist users see elevated errors."])
        XCTAssertEqual(status.pageURL, URL(string: "https://status.cloud.google.com"))
    }

    func testGoogleCloudNothingOpen() throws {
        let json = """
        [{"external_desc": "Done", "end": "2026-11-01T12:00:00+00:00", "affected_products": [{"title": "Gemini Code Assist"}]}]
        """
        XCTAssertEqual(try ServiceStatus.googleCloud(incidents: data(json), page: gemini).level, .operational)
    }

    func testPagesPerAgent() {
        XCTAssertEqual(ProviderID.copilot.statusPage?.pageURL, URL(string: "https://www.githubstatus.com"))
        XCTAssertNotNil(ProviderID.codex.statusPage)
        // status.x.ai turns automated clients away.
        XCTAssertNil(ProviderID.cursor.statusPage)
        XCTAssertNil(ProviderID.grokBot.statusPage)
    }

    func testComponentNamesMatchByPrefixIgnoringCase() {
        XCTAssertTrue(claude.matches("Claude API (api.anthropic.com)"))
        XCTAssertTrue(claude.matches("CLAUDE CODE"))
        XCTAssertFalse(claude.matches("Claude Console (platform.claude.com)"))
        XCTAssertFalse(claude.matches("claude.ai"))
        XCTAssertFalse(claude.matches("Claude Cowork"))
        XCTAssertFalse(claude.matches(nil))
    }

    func testPortugueseTexts() {
        AppLanguage.current = .portuguese
        let status = ServiceStatus(level: .degraded, components: ["Copilot"], pageURL: URL(string: "https://www.githubstatus.com")!)
        XCTAssertEqual(status.title(for: .copilot), "GitHub Copilot com instabilidade")
        XCTAssertEqual(status.detailLines, ["Afetado: Copilot"])
        XCTAssertEqual(ServiceStatus.linkTitle, "Ver página de status")
    }

    func testMenuBarSegmentCarriesTheBadge() {
        let item = LimitItem(id: "s", kind: "session", percent: 40, severity: .normal, resetsAt: nil, group: .session)
        let snapshot = ProviderSnapshot(provider: .claude, planName: nil, items: [item])
        XCTAssertEqual(MenuBarSegment.make(provider: .claude, display: .session, snapshot: snapshot, hasError: false, alert: .critical)?.alert, .critical)
        XCTAssertEqual(MenuBarSegment.make(provider: .claude, display: .session, snapshot: nil, hasError: false, alert: .warning)?.alert, .warning)
        XCTAssertNil(MenuBarSegment.make(provider: .claude, display: .session, snapshot: snapshot, hasError: false)?.alert)
        XCTAssertNil(MenuBarSegment.make(provider: .claude, display: .hidden, snapshot: snapshot, hasError: false, alert: .warning))
    }
}
