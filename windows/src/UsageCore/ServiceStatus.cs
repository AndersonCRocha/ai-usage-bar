using System.Net;
using System.Text.Json.Nodes;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

/// <summary>How a service's public status page rates the parts of it an agent relies on, from best to worst.</summary>
public enum StatusLevel { Operational, Maintenance, Degraded, PartialOutage, MajorOutage }

/// <summary>The status pages' own words, as a <see cref="StatusLevel"/>. Same rules as the Mac app.</summary>
public static class StatusLevels
{
    /// <summary>
    /// A component's state: "operational", "under_maintenance", "degraded_performance",
    /// "partial_outage", "major_outage" (incident.io's compatible feed may say "full_outage").
    /// </summary>
    public static StatusLevel Component(string? status)
    {
        var value = status?.ToLowerInvariant() ?? "";
        if (value.Contains("major") || value.Contains("full")) return StatusLevel.MajorOutage;
        if (value.Contains("partial")) return StatusLevel.PartialOutage;
        if (value.Contains("degraded")) return StatusLevel.Degraded;
        if (value.Contains("maintenance")) return StatusLevel.Maintenance;
        return StatusLevel.Operational;
    }

    /// <summary>
    /// An open incident's impact ("none", "minor", "major", "critical"): an open incident
    /// on a part that matters counts as degraded at least, whatever its impact says.
    /// </summary>
    public static StatusLevel Impact(string? impact) => impact?.ToLowerInvariant() switch
    {
        "critical" => StatusLevel.MajorOutage,
        "major" => StatusLevel.PartialOutage,
        "maintenance" => StatusLevel.Maintenance,
        _ => StatusLevel.Degraded,
    };

    /// <summary>The whole page's indicator ("none", "minor", "major", "critical", "maintenance").</summary>
    public static StatusLevel Indicator(string? indicator) => indicator?.ToLowerInvariant() switch
    {
        "critical" => StatusLevel.MajorOutage,
        "major" => StatusLevel.PartialOutage,
        "minor" => StatusLevel.Degraded,
        "maintenance" => StatusLevel.Maintenance,
        _ => StatusLevel.Operational,
    };

    /// <summary>Google Cloud's <c>status_impact</c>.</summary>
    public static StatusLevel GoogleImpact(string? impact) => impact?.ToUpperInvariant() switch
    {
        "SERVICE_OUTAGE" => StatusLevel.MajorOutage,
        "SERVICE_DISRUPTION" => StatusLevel.PartialOutage,
        _ => StatusLevel.Degraded,
    };
}

/// <summary>What a service's status page says right now about the parts an agent relies on.</summary>
/// <param name="Components">The parts that are not working normally ("claude.ai"), in the page's order.</param>
/// <param name="Incidents">Titles of the open incidents on those parts.</param>
/// <param name="PageUrl">The page people open to read more.</param>
public sealed record ServiceStatus(StatusLevel Level, IReadOnlyList<string> Components, IReadOnlyList<string> Incidents, Uri PageUrl)
{
    /// <summary>Worth a badge in the tray and a warning on the tab. Maintenance is only shown in the popup.</summary>
    public bool IsProblem => Level >= StatusLevel.Degraded;

    /// <summary>Anything to show at all.</summary>
    public bool IsNotice => Level != StatusLevel.Operational;

    public Severity Severity => Level == StatusLevel.MajorOutage ? Severity.Critical : Severity.Warning;

    public string Title(ProviderId provider)
    {
        var name = provider.DisplayName();
        return Level switch
        {
            StatusLevel.Operational => T($"{name} funcionando normalmente", $"{name} is operating normally"),
            StatusLevel.Maintenance => T($"{name} em manutenção", $"{name} is under maintenance"),
            StatusLevel.MajorOutage => T($"{name} fora do ar", $"{name} is down"),
            _ => T($"{name} com instabilidade", $"{name} is having issues"),
        };
    }

    /// <summary>The open incidents, then which parts are affected.</summary>
    public IReadOnlyList<string> DetailLines
    {
        get
        {
            var lines = new List<string>(Incidents);
            if (Components.Count > 0) lines.Add(T("Afetado: ", "Affected: ") + string.Join(", ", Components));
            return lines;
        }
    }

    public static string LinkTitle => T("Ver página de status", "View status page");

    /// <summary>
    /// A made-up status for screenshots (<c>--service-status claude=degraded</c>), so the banner and the badge
    /// can be seen without a real outage. <paramref name="level"/>: maintenance, degraded, partial or major.
    /// </summary>
    public static ServiceStatus? Preview(ProviderId provider, string level)
    {
        StatusLevel? parsed = level.ToLowerInvariant() switch
        {
            "maintenance" => StatusLevel.Maintenance,
            "degraded" => StatusLevel.Degraded,
            "partial" => StatusLevel.PartialOutage,
            "major" => StatusLevel.MajorOutage,
            _ => null,
        };
        return parsed is { } value
            ? new ServiceStatus(value, [], ["Elevated error rates"], provider.StatusPage()?.PageUrl ?? new Uri("https://status.claude.com"))
            : null;
    }

    /// <summary>What <c>--dump</c> prints: "Claude is having issues — Elevated error rates; Affected: claude.ai".</summary>
    public string Summary(ProviderId provider) =>
        Title(provider) + (DetailLines.Count > 0 ? " — " + string.Join("; ", DetailLines) : "");

    /// <summary>Compares the lists by content, so an unchanged status does not redraw the tray.</summary>
    public bool Equals(ServiceStatus? other) =>
        other is not null && Level == other.Level && PageUrl == other.PageUrl
        && Components.SequenceEqual(other.Components) && Incidents.SequenceEqual(other.Incidents);

    public override int GetHashCode() => HashCode.Combine(Level, PageUrl, Components.Count, Incidents.Count);

    // MARK: Reading the feeds

    /// <summary>
    /// A Statuspage summary (and, for incident.io, its full component list). Only the parts
    /// in <see cref="StatusPage.Components"/> count; if none of them is on the page any more
    /// (renamed), the whole page's indicator is used so a problem is not missed.
    /// </summary>
    /// <exception cref="UsageException">The summary is not a JSON object.</exception>
    public static ServiceStatus Statuspage(byte[] summary, byte[]? components, StatusPage page)
    {
        if (Json.Parse(summary) is not JsonObject root) throw new UsageException(UsageErrorKind.Decoding);
        var listed = components is not null && Json.Parse(components) is JsonObject full ? StatuspageComponents(full) : [];
        var all = listed.Count > 0 ? listed : StatuspageComponents(root);
        var parts = all.Where(c => c.Group != true).ToList();
        var open = Objects(Json.Arr(root, "incidents")).Select(StatuspageIncident.From).Where(i => i.IsOpen).ToList();

        var relevant = parts.Where(c => page.Matches(c.Name)).ToList();
        if (relevant.Count == 0)
        {
            return new ServiceStatus(
                StatusLevels.Indicator(Json.Str(Json.Obj(root, "status"), "indicator")),
                Names(parts.Where(c => StatusLevels.Component(c.Status) != StatusLevel.Operational)),
                [.. open.Select(i => i.Name).OfType<string>().Where(name => name.Length > 0)],
                page.PageUrl);
        }

        var touching = open.Where(incident => incident.Components.Any(c => page.Matches(c.Name))).ToList();
        var level = Max(
            relevant.Select(c => StatusLevels.Component(c.Status)).DefaultIfEmpty(StatusLevel.Operational).Max(),
            touching.Select(i => StatusLevels.Impact(i.Impact)).DefaultIfEmpty(StatusLevel.Operational).Max());
        return new ServiceStatus(
            level,
            Names(relevant.Where(c => StatusLevels.Component(c.Status) != StatusLevel.Operational)),
            [.. touching.Select(i => i.Name).OfType<string>().Where(name => name.Length > 0)],
            page.PageUrl);
    }

    /// <summary>Google Cloud's incident list: the incidents with no end yet on the products in <see cref="StatusPage.Components"/>.</summary>
    /// <exception cref="UsageException">The list is not a JSON array.</exception>
    public static ServiceStatus GoogleCloud(byte[] incidents, StatusPage page)
    {
        if (Json.Parse(incidents) is not JsonArray root) throw new UsageException(UsageErrorKind.Decoding);
        var open = Objects(root)
            .Select(GoogleIncident.From)
            .Where(incident => string.IsNullOrEmpty(incident.End) && incident.Products.Any(p => p.Matches(page)))
            .ToList();
        var products = open.SelectMany(incident => incident.Products.Where(p => p.Matches(page)).Select(p => p.Title).OfType<string>());
        return new ServiceStatus(
            open.Select(i => StatusLevels.GoogleImpact(i.StatusImpact)).DefaultIfEmpty(StatusLevel.Operational).Max(),
            Unique(products),
            [.. open.Select(i => i.Description?.Trim()).OfType<string>().Where(text => text.Length > 0)],
            page.PageUrl);
    }

    private static StatusLevel Max(StatusLevel a, StatusLevel b) => a > b ? a : b;

    /// <summary>The objects of a JSON array: anything else in it is skipped, as a malformed element.</summary>
    private static IEnumerable<JsonObject> Objects(JsonArray? array) => array?.OfType<JsonObject>() ?? [];

    private static List<StatuspageComponent> StatuspageComponents(JsonNode? root) =>
        [.. Objects(Json.Arr(root, "components")).Select(StatuspageComponent.From)];

    private static IReadOnlyList<string> Names(IEnumerable<StatuspageComponent> components) =>
        Unique(components.Select(c => c.Name).OfType<string>());

    private static IReadOnlyList<string> Unique(IEnumerable<string> names)
    {
        var seen = new HashSet<string>();
        return [.. names.Where(seen.Add)];
    }

    private sealed record StatuspageComponent(string? Name, string? Status, bool? Group)
    {
        public static StatuspageComponent From(JsonObject node) =>
            new(Json.Str(node, "name"), Json.Str(node, "status"), Json.Bool(node, "group"));
    }

    private sealed record StatuspageIncident(string? Name, string? Status, string? Impact, IReadOnlyList<StatuspageComponent> Components)
    {
        public bool IsOpen => Status?.ToLowerInvariant() is not ("resolved" or "postmortem" or "completed");

        public static StatuspageIncident From(JsonObject node) => new(
            Json.Str(node, "name"),
            Json.Str(node, "status"),
            Json.Str(node, "impact"),
            StatuspageComponents(node));
    }

    private sealed record GoogleProduct(string? Title, string? Id)
    {
        public bool Matches(StatusPage page) => page.Matches(Id) || page.Matches(Title);
    }

    private sealed record GoogleIncident(string? Description, string? End, string? StatusImpact, IReadOnlyList<GoogleProduct> Products)
    {
        public static GoogleIncident From(JsonObject node) => new(
            Json.Str(node, "external_desc"),
            Json.Str(node, "end"),
            Json.Str(node, "status_impact"),
            [.. Objects(Json.Arr(node, "affected_products")).Select(p => new GoogleProduct(Json.Str(p, "title"), Json.Str(p, "id")))]);
    }
}

/// <summary>Where a status page's data is read from.</summary>
public abstract record StatusFeed
{
    /// <summary>
    /// Atlassian Statuspage's <c>/api/v2/summary.json</c>, which incident.io pages serve too.
    /// <paramref name="Components"/>: incident.io's summary lists only the first 25 components,
    /// so its <c>/api/v2/components.json</c> is read for the full list.
    /// </summary>
    public sealed record Statuspage(Uri Summary, Uri? Components = null) : StatusFeed;

    /// <summary>Google Cloud's <c>incidents.json</c>.</summary>
    public sealed record GoogleCloud(Uri Incidents) : StatusFeed;
}

/// <summary>Where an agent's service publishes its status, and which of its parts matter.</summary>
/// <param name="Components">
/// Component names, ignoring case, a name that starts with one matching too ("Claude API"
/// matches "Claude API (api.anthropic.com)"); on Google Cloud, product ids or titles.
/// </param>
public sealed record StatusPage(Uri PageUrl, StatusFeed Feed, IReadOnlyList<string> Components)
{
    internal bool Matches(string? name) =>
        name is not null && Components.Any(component => name.StartsWith(component, StringComparison.OrdinalIgnoreCase));
}

public static class StatusPages
{
    /// <summary>The service's public status page; null when there is none the app can read.</summary>
    public static StatusPage? StatusPage(this ProviderId provider) => provider switch
    {
        ProviderId.Claude => new(
            new Uri("https://status.claude.com"),
            new StatusFeed.Statuspage(new Uri("https://status.claude.com/api/v2/summary.json")),
            ["Claude Code", "claude.ai", "Claude API"]),
        ProviderId.Codex => new(
            new Uri("https://status.openai.com"),
            new StatusFeed.Statuspage(
                new Uri("https://status.openai.com/api/v2/summary.json"),
                new Uri("https://status.openai.com/api/v2/components.json")),
            ["Codex", "CLI", "VS Code extension"]),
        ProviderId.Copilot => new(
            new Uri("https://www.githubstatus.com"),
            new StatusFeed.Statuspage(new Uri("https://www.githubstatus.com/api/v2/summary.json")),
            ["Copilot"]),
        // Gemini CLI signed in with Google runs on Gemini Code Assist.
        ProviderId.Gemini => new(
            new Uri("https://status.cloud.google.com"),
            new StatusFeed.GoogleCloud(new Uri("https://status.cloud.google.com/incidents.json")),
            ["deUeOEPYanfJ9w8cpyBJ", "Gemini Code Assist"]),
        // Their status page (status.cursor.com redirects to status.x.ai) turns automated clients away.
        _ => null,
    };
}

/// <summary>
/// When the status pages are read, kept free of UI so it can be tested. Independent of the usage
/// refresh and its rate-limit waits: these are other servers.
/// </summary>
public static class StatusSchedule
{
    /// <summary>How often the status pages are read by themselves.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>A page is not read again sooner than this (opening the popup, a change of agents)…</summary>
    public static readonly TimeSpan MinWait = TimeSpan.FromSeconds(120);

    /// <summary>…or than this when the refresh button asks.</summary>
    public static readonly TimeSpan ForcedMinWait = TimeSpan.FromSeconds(15);

    /// <summary>A failed read keeps the last status this long after the last successful one.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(30);

    public static bool IsDue(DateTimeOffset? lastAttempt, DateTimeOffset now, bool force = false) =>
        lastAttempt is not { } asked || now - asked >= (force ? ForcedMinWait : MinWait);

    /// <summary>
    /// What is shown after a failed read: the last status, so one bad answer does not make a badge
    /// blink, unless it is too old to trust (or there never was one).
    /// </summary>
    public static ServiceStatus? AfterFailure(ServiceStatus? previous, DateTimeOffset? checkedAt, DateTimeOffset now) =>
        checkedAt is { } at && now - at <= KeptFor ? previous : null;
}

// MARK: Fetching

public interface IServiceStatusFetcher
{
    Task<ServiceStatus> FetchAsync(StatusPage page, CancellationToken cancellation = default);
}

/// <summary>Reads public status pages: no login, nothing about the user is sent.</summary>
public sealed class ServiceStatusClient : IServiceStatusFetcher
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly HttpClient client;

    public ServiceStatusClient(HttpClient? client = null) => this.client = client ?? SharedClient;

    public async Task<ServiceStatus> FetchAsync(StatusPage page, CancellationToken cancellation = default)
    {
        switch (page.Feed)
        {
            case StatusFeed.Statuspage feed:
                var summary = GetAsync(feed.Summary, cancellation);
                // The full component list is a bonus: without it the summary's first 25 are used.
                var components = feed.Components is { } url ? TryGetAsync(url, cancellation) : Task.FromResult<byte[]?>(null);
                return ServiceStatus.Statuspage(await summary.ConfigureAwait(false), await components.ConfigureAwait(false), page);
            case StatusFeed.GoogleCloud feed:
                return ServiceStatus.GoogleCloud(await GetAsync(feed.Incidents, cancellation).ConfigureAwait(false), page);
            default:
                throw new UsageException(UsageErrorKind.Decoding);
        }
    }

    private async Task<byte[]> GetAsync(Uri url, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await Http.SendAsync(request, cancellation, client).ConfigureAwait(false);
    }

    private async Task<byte[]?> TryGetAsync(Uri url, CancellationToken cancellation)
    {
        try { return await GetAsync(url, cancellation).ConfigureAwait(false); }
        catch (UsageException) { return null; }
    }
}
