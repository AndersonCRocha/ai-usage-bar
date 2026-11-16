import XCTest
@testable import AIUsageBar
@testable import UsageCore

/// An agent that always loads.
private struct WorkingProvider: UsageProvider {
    let id: ProviderID

    func isAvailable() -> Bool { true }

    func fetch() async throws -> ProviderFetchResult {
        let item = LimitItem(id: "session", kind: "session", percent: 40, severity: .normal, resetsAt: nil, group: .session)
        return ProviderFetchResult(snapshot: ProviderSnapshot(provider: id, planName: "Max", items: [item]), cache: Data())
    }

    func snapshot(fromCache data: Data) -> ProviderSnapshot? { nil }
}

/// Plays back status page answers (nil: the page did not answer) and counts the reads per page.
private final class ScriptedStatus: ServiceStatusFetching, @unchecked Sendable {
    private let lock = NSLock()
    private var answers: [ServiceStatus?]
    private var reads: [URL: Int] = [:]

    init(_ answers: [ServiceStatus?]) { self.answers = answers }

    func calls(_ id: ProviderID) -> Int { lock.withLock { reads[id.statusPage!.pageURL] ?? 0 } }

    func setAnswers(_ answers: [ServiceStatus?]) { lock.withLock { self.answers = answers } }

    func fetch(_ page: StatusPage) async throws -> ServiceStatus {
        let answer: ServiceStatus? = lock.withLock {
            reads[page.pageURL, default: 0] += 1
            return answers.count > 1 ? answers.removeFirst() : answers.first ?? nil
        }
        guard let answer else { throw UsageError.network("offline") }
        return answer
    }
}

@MainActor
final class ServiceStatusRefreshTests: XCTestCase {
    private var defaults: UserDefaults!
    private var suite: String!
    private var time = Date(timeIntervalSince1970: 1_800_000_000)

    private let degraded = ServiceStatus(
        level: .degraded, components: ["Claude Code"], incidents: ["Elevated errors"], pageURL: URL(string: "https://status.claude.com")!
    )
    private let operational = ServiceStatus(level: .operational, pageURL: URL(string: "https://status.claude.com")!)

    override func setUp() async throws {
        suite = "ServiceStatusRefreshTests-\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)
        defaults.set("en", forKey: "language")
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suite)
    }

    private func makeStore(_ ids: [ProviderID], status: ScriptedStatus) async -> UsageStore {
        let store = UsageStore(providers: ids.map(WorkingProvider.init), defaults: defaults, statusClient: status)
        store.clock = { [unowned self] in self.time }
        await store.refresh()
        return store
    }

    func testProblemShowsInTheMenuBarTabAndTooltip() async {
        let status = ScriptedStatus([degraded])
        let store = await makeStore([.claude], status: status)
        await store.refreshStatus()

        let state = store.state(for: .claude)
        XCTAssertEqual(state.serviceStatus, degraded)
        XCTAssertTrue(state.hasOutage)
        XCTAssertFalse(state.hasWarning, "a status page problem is not a fetch error")
        XCTAssertTrue(store.hasOutage)
        XCTAssertEqual(store.menuBarSegments.first?.alert, .warning)
        XCTAssertTrue(store.tooltip.contains("⚠ Claude is having issues"))
        XCTAssertTrue(store.dumpText.contains("Status: Claude is having issues — Elevated errors; Affected: Claude Code"))
    }

    func testOperationalShowsNothing() async {
        let store = await makeStore([.claude], status: ScriptedStatus([operational]))
        await store.refreshStatus()
        XCTAssertFalse(store.state(for: .claude).hasOutage)
        XCTAssertNil(store.menuBarSegments.first?.alert)
    }

    func testMaintenanceIsNotABadge() async {
        let maintenance = ServiceStatus(level: .maintenance, pageURL: URL(string: "https://status.claude.com")!)
        let store = await makeStore([.claude], status: ScriptedStatus([maintenance]))
        await store.refreshStatus()
        XCTAssertEqual(store.state(for: .claude).serviceStatus?.level, .maintenance)
        XCTAssertNil(store.menuBarSegments.first?.alert)
    }

    func testReadsAgainOnlyWhenStale() async {
        let status = ScriptedStatus([operational])
        let store = await makeStore([.claude], status: status)
        await store.refreshStatus()
        XCTAssertEqual(status.calls(.claude), 1)

        time += 60
        await store.refreshStatus()
        XCTAssertEqual(status.calls(.claude), 1, "read a minute ago")

        await store.refreshStatus(force: true)
        XCTAssertEqual(status.calls(.claude), 2, "the refresh button reads it again")

        time += 5
        await store.refreshStatus(force: true)
        XCTAssertEqual(status.calls(.claude), 2, "not twice in a few seconds")

        time += UsageStore.statusStaleAfter
        await store.refreshStatus()
        XCTAssertEqual(status.calls(.claude), 3)
    }

    func testKeepsTheLastAnswerForAWhileWhenThePageDoesNotAnswer() async {
        let status = ScriptedStatus([degraded])
        let store = await makeStore([.claude], status: status)
        await store.refreshStatus()
        status.setAnswers([nil])

        time += UsageStore.statusInterval
        await store.refreshStatus()
        XCTAssertEqual(store.state(for: .claude).serviceStatus, degraded)

        time += UsageStore.statusExpiresAfter
        await store.refreshStatus()
        XCTAssertNil(store.state(for: .claude).serviceStatus, "too old to trust")
    }

    func testAgentsWithoutAReadablePageAreNotAsked() async {
        let status = ScriptedStatus([degraded])
        let store = await makeStore([.claude, .cursor, .grokBot], status: status)
        await store.refreshStatus()
        XCTAssertEqual(status.calls(.claude), 1)
        XCTAssertNil(store.state(for: .cursor).serviceStatus)
        XCTAssertNil(store.state(for: .grokBot).serviceStatus)
    }

    func testNoClientNoReads() async {
        let store = UsageStore(providers: [WorkingProvider(id: .claude)], defaults: defaults)
        await store.refresh()
        await store.refreshStatus(force: true)
        XCTAssertNil(store.state(for: .claude).serviceStatus)
    }
}
