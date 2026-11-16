import Foundation

/// How a service's public status page rates the parts of it an agent relies on, from best to worst.
public enum StatusLevel: Int, Comparable, Sendable {
    case operational, maintenance, degraded, partialOutage, majorOutage

    public static func < (lhs: Self, rhs: Self) -> Bool { lhs.rawValue < rhs.rawValue }

    /// A component's state: "operational", "under_maintenance", "degraded_performance",
    /// "partial_outage", "major_outage" (incident.io's compatible feed may say "full_outage").
    init(component status: String?) {
        let value = status?.lowercased() ?? ""
        if value.contains("major") || value.contains("full") { self = .majorOutage }
        else if value.contains("partial") { self = .partialOutage }
        else if value.contains("degraded") { self = .degraded }
        else if value.contains("maintenance") { self = .maintenance }
        else { self = .operational }
    }

    /// An open incident's impact ("none", "minor", "major", "critical"): an open incident
    /// on a part that matters counts as degraded at least, whatever its impact says.
    init(impact: String?) {
        switch impact?.lowercased() {
        case "critical": self = .majorOutage
        case "major": self = .partialOutage
        case "maintenance": self = .maintenance
        default: self = .degraded
        }
    }

    /// The whole page's indicator ("none", "minor", "major", "critical", "maintenance").
    init(indicator: String?) {
        switch indicator?.lowercased() {
        case "critical": self = .majorOutage
        case "major": self = .partialOutage
        case "minor": self = .degraded
        case "maintenance": self = .maintenance
        default: self = .operational
        }
    }

    /// Google Cloud's `status_impact`.
    init(googleImpact: String?) {
        switch googleImpact?.uppercased() {
        case "SERVICE_OUTAGE": self = .majorOutage
        case "SERVICE_DISRUPTION": self = .partialOutage
        default: self = .degraded
        }
    }
}

/// What a service's status page says right now about the parts an agent relies on.
public struct ServiceStatus: Equatable, Sendable {
    public var level: StatusLevel
    /// The parts that are not working normally ("claude.ai"), in the page's order.
    public var components: [String]
    /// Titles of the open incidents on those parts.
    public var incidents: [String]
    /// The page people open to read more.
    public var pageURL: URL

    public init(level: StatusLevel, components: [String] = [], incidents: [String] = [], pageURL: URL) {
        self.level = level
        self.components = components
        self.incidents = incidents
        self.pageURL = pageURL
    }

    /// Worth a badge in the menu bar and a warning on the tab. Maintenance is only shown in the popover.
    public var isProblem: Bool { level >= .degraded }

    /// Anything to show at all.
    public var isNotice: Bool { level != .operational }

    public var severity: Severity { level == .majorOutage ? .critical : .warning }

    public func title(for provider: ProviderID) -> String {
        let name = provider.displayName
        switch level {
        case .operational: return tr("\(name) funcionando normalmente", "\(name) is operating normally")
        case .maintenance: return tr("\(name) em manutenção", "\(name) is under maintenance")
        case .degraded, .partialOutage: return tr("\(name) com instabilidade", "\(name) is having issues")
        case .majorOutage: return tr("\(name) fora do ar", "\(name) is down")
        }
    }

    /// The open incidents, then which parts are affected.
    public var detailLines: [String] {
        var lines = incidents
        if !components.isEmpty {
            lines.append(tr("Afetado: ", "Affected: ") + components.joined(separator: ", "))
        }
        return lines
    }

    public static var linkTitle: String { tr("Ver página de status", "View status page") }
}

/// Where an agent's service publishes its status, and which of its parts matter.
public struct StatusPage: Equatable, Sendable {
    public enum Feed: Equatable, Sendable {
        /// Atlassian Statuspage's `/api/v2/summary.json`, which incident.io pages serve too.
        /// `components`: incident.io's summary lists only the first 25 components, so its
        /// `/api/v2/components.json` is read for the full list.
        case statuspage(summary: URL, components: URL?)
        /// Google Cloud's `incidents.json`.
        case googleCloud(incidents: URL)
    }

    public var pageURL: URL
    public var feed: Feed
    /// Component names, ignoring case, a name that starts with one matching too ("Claude API"
    /// matches "Claude API (api.anthropic.com)"); on Google Cloud, product ids or titles.
    public var components: [String]

    public init(pageURL: URL, feed: Feed, components: [String]) {
        self.pageURL = pageURL
        self.feed = feed
        self.components = components
    }

    func matches(_ name: String?) -> Bool {
        guard let name = name?.lowercased() else { return false }
        return components.contains { name.hasPrefix($0.lowercased()) }
    }
}

extension ProviderID {
    /// The service's public status page; nil when there is none the app can read.
    public var statusPage: StatusPage? {
        switch self {
        case .claude:
            return StatusPage(
                pageURL: URL(string: "https://status.claude.com")!,
                feed: .statuspage(summary: URL(string: "https://status.claude.com/api/v2/summary.json")!, components: nil),
                components: ["Claude Code", "claude.ai", "Claude API"]
            )
        case .codex:
            return StatusPage(
                pageURL: URL(string: "https://status.openai.com")!,
                feed: .statuspage(
                    summary: URL(string: "https://status.openai.com/api/v2/summary.json")!,
                    components: URL(string: "https://status.openai.com/api/v2/components.json")!
                ),
                components: ["Codex", "CLI", "VS Code extension"]
            )
        case .copilot:
            return StatusPage(
                pageURL: URL(string: "https://www.githubstatus.com")!,
                feed: .statuspage(summary: URL(string: "https://www.githubstatus.com/api/v2/summary.json")!, components: nil),
                components: ["Copilot"]
            )
        case .gemini:
            // Gemini CLI signed in with Google runs on Gemini Code Assist.
            return StatusPage(
                pageURL: URL(string: "https://status.cloud.google.com")!,
                feed: .googleCloud(incidents: URL(string: "https://status.cloud.google.com/incidents.json")!),
                components: ["deUeOEPYanfJ9w8cpyBJ", "Gemini Code Assist"]
            )
        case .cursor, .grokBot:
            // Their status page (status.cursor.com redirects to status.x.ai) turns automated clients away.
            return nil
        }
    }
}

// MARK: Reading the feeds

private struct StatuspageComponent: Decodable {
    var name: String?
    var status: String?
    var group: Bool?
}

private struct StatuspageIncident: Decodable {
    var name: String?
    var status: String?
    var impact: String?
    var components: [StatuspageComponent]?

    enum CodingKeys: String, CodingKey { case name, status, impact, components }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        name = try? c.decodeIfPresent(String.self, forKey: .name)
        status = try? c.decodeIfPresent(String.self, forKey: .status)
        impact = try? c.decodeIfPresent(String.self, forKey: .impact)
        components = (try? c.decodeIfPresent(LossyArray<StatuspageComponent>.self, forKey: .components))?.elements
    }

    var isOpen: Bool {
        !["resolved", "postmortem", "completed"].contains(status?.lowercased() ?? "")
    }
}

private struct StatuspageSummary: Decodable {
    struct Indicator: Decodable { var indicator: String? }

    var status: Indicator?
    var components: [StatuspageComponent]?
    var incidents: [StatuspageIncident]?

    enum CodingKeys: String, CodingKey { case status, components, incidents }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        status = try? c.decodeIfPresent(Indicator.self, forKey: .status)
        components = (try? c.decodeIfPresent(LossyArray<StatuspageComponent>.self, forKey: .components))?.elements
        incidents = (try? c.decodeIfPresent(LossyArray<StatuspageIncident>.self, forKey: .incidents))?.elements
    }
}

private struct StatuspageComponents: Decodable {
    var components: [StatuspageComponent]

    enum CodingKeys: String, CodingKey { case components }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        components = (try? c.decodeIfPresent(LossyArray<StatuspageComponent>.self, forKey: .components))?.elements ?? []
    }
}

private struct GoogleIncident: Decodable {
    struct Product: Decodable {
        var title: String?
        var id: String?
    }

    var externalDesc: String?
    var end: String?
    var statusImpact: String?
    var affectedProducts: [Product]?

    enum CodingKeys: String, CodingKey {
        case externalDesc = "external_desc", end, statusImpact = "status_impact", affectedProducts = "affected_products"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        externalDesc = try? c.decodeIfPresent(String.self, forKey: .externalDesc)
        end = try? c.decodeIfPresent(String.self, forKey: .end)
        statusImpact = try? c.decodeIfPresent(String.self, forKey: .statusImpact)
        affectedProducts = (try? c.decodeIfPresent(LossyArray<Product>.self, forKey: .affectedProducts))?.elements
    }
}

extension ServiceStatus {
    /// A Statuspage summary (and, for incident.io, its full component list). Only the parts
    /// in `page.components` count; if none of them is on the page any more (renamed), the
    /// whole page's indicator is used so a problem is not missed.
    public static func statuspage(summary: Data, components: Data? = nil, page: StatusPage) throws -> ServiceStatus {
        guard let decoded = try? JSONDecoder().decode(StatuspageSummary.self, from: summary) else { throw UsageError.decoding }
        let listed = components.flatMap { try? JSONDecoder().decode(StatuspageComponents.self, from: $0).components }
        let all = (listed?.isEmpty == false ? listed : nil) ?? decoded.components ?? []
        let parts = all.filter { $0.group != true }
        let open = (decoded.incidents ?? []).filter(\.isOpen)

        let relevant = parts.filter { page.matches($0.name) }
        guard !relevant.isEmpty else {
            return ServiceStatus(
                level: StatusLevel(indicator: decoded.status?.indicator),
                components: names(parts.filter { StatusLevel(component: $0.status) != .operational }),
                incidents: open.compactMap(\.name).filter { !$0.isEmpty },
                pageURL: page.pageURL
            )
        }

        let touching = open.filter { incident in incident.components?.contains { page.matches($0.name) } ?? false }
        let level = max(
            relevant.map { StatusLevel(component: $0.status) }.max() ?? .operational,
            touching.map { StatusLevel(impact: $0.impact) }.max() ?? .operational
        )
        return ServiceStatus(
            level: level,
            components: names(relevant.filter { StatusLevel(component: $0.status) != .operational }),
            incidents: touching.compactMap(\.name).filter { !$0.isEmpty },
            pageURL: page.pageURL
        )
    }

    /// Google Cloud's incident list: the incidents with no end yet on the products in `page.components`.
    public static func googleCloud(incidents data: Data, page: StatusPage) throws -> ServiceStatus {
        guard let incidents = try? JSONDecoder().decode(LossyArray<GoogleIncident>.self, from: data).elements else {
            throw UsageError.decoding
        }
        let open = incidents.filter { incident in
            (incident.end ?? "").isEmpty
                && (incident.affectedProducts ?? []).contains { page.matches($0.id) || page.matches($0.title) }
        }
        let products = open.flatMap { incident in
            (incident.affectedProducts ?? [])
                .filter { page.matches($0.id) || page.matches($0.title) }
                .compactMap(\.title)
        }
        return ServiceStatus(
            level: open.map { StatusLevel(googleImpact: $0.statusImpact) }.max() ?? .operational,
            components: unique(products),
            incidents: open.compactMap { $0.externalDesc?.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty },
            pageURL: page.pageURL
        )
    }

    private static func names(_ components: [StatuspageComponent]) -> [String] {
        unique(components.compactMap(\.name))
    }

    private static func unique(_ names: [String]) -> [String] {
        var seen = Set<String>()
        return names.filter { seen.insert($0).inserted }
    }
}

// MARK: Fetching

public protocol ServiceStatusFetching: Sendable {
    func fetch(_ page: StatusPage) async throws -> ServiceStatus
}

/// Reads public status pages: no login, nothing about the user is sent.
public struct ServiceStatusClient: ServiceStatusFetching {
    public init() {}

    public func fetch(_ page: StatusPage) async throws -> ServiceStatus {
        switch page.feed {
        case .statuspage(let summaryURL, let componentsURL):
            async let summary = get(summaryURL)
            var components: Data?
            if let componentsURL { components = try? await get(componentsURL) }
            return try ServiceStatus.statuspage(summary: try await summary, components: components, page: page)
        case .googleCloud(let url):
            return try ServiceStatus.googleCloud(incidents: try await get(url), page: page)
        }
    }

    private func get(_ url: URL) async throws -> Data {
        var request = URLRequest(url: url, cachePolicy: .useProtocolCachePolicy, timeoutInterval: 15)
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("ai-usage-bar", forHTTPHeaderField: "User-Agent")
        return try await HTTP.send(request)
    }
}
