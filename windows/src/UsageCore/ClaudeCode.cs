using System.Text.Json.Nodes;

namespace AIUsageBar.Core;

/// <summary>
/// The Claude plan limits as Claude Code sees them. Claude Code hands them to its status line
/// command after every model response (<c>rate_limits</c> in the JSON on stdin), so reading them
/// costs no request and is never rate limited. Only the session (5 h) and weekly limits, only for
/// Pro, Max and Ultra, and only while Claude Code runs in a terminal. Same rules as the Mac app.
/// </summary>
public sealed record ClaudeCodeReading(ClaudeCodeReading.Window? Session = null, ClaudeCodeReading.Window? Weekly = null)
{
    /// <param name="SeenAt">
    /// When Claude Code got this number: when its session last wrote its transcript. An idle session
    /// repeats what it saw long ago (after a settings change, for one).
    /// </param>
    public sealed record Window(double Percent, DateTimeOffset ResetsAt, DateTimeOffset SeenAt);

    public bool IsEmpty => Session is null && Weekly is null;

    /// <summary>Limits at most this far apart reset at the same moment, so they belong to the same period.</summary>
    internal static readonly TimeSpan SamePeriodTolerance = TimeSpan.FromMinutes(10);

    internal static bool SamePeriod(DateTimeOffset a, DateTimeOffset b) => (a - b).Duration() <= SamePeriodTolerance;

    /// <summary>Without the windows whose reset already passed.</summary>
    public ClaudeCodeReading Current(DateTimeOffset now) =>
        new(Session is { } s && s.ResetsAt > now ? s : null, Weekly is { } w && w.ResetsAt > now ? w : null);

    /// <summary>
    /// Many sessions report, and an idle one repeats an old number. Usage only grows within a
    /// period, so the later period wins and, within one period, the higher number.
    /// </summary>
    public ClaudeCodeReading Merged(ClaudeCodeReading other, DateTimeOffset now) =>
        new(Pick(Session, other.Session, now), Pick(Weekly, other.Weekly, now));

    internal static Window? Pick(Window? a, Window? b, DateTimeOffset now)
    {
        if (a is not null && a.ResetsAt <= now) a = null;
        if (b is not null && b.ResetsAt <= now) b = null;
        if (a is null) return b;
        if (b is null) return a;
        if (!SamePeriod(a.ResetsAt, b.ResetsAt)) return a.ResetsAt > b.ResetsAt ? a : b;
        if (a.Percent != b.Percent) return a.Percent > b.Percent ? a : b;
        return a.SeenAt >= b.SeenAt ? a : b;
    }

    /// <summary>
    /// The <c>rate_limits</c> in the JSON a status line command gets on stdin; null when there are none
    /// (an API key login, or before the session's first response). <paramref name="modificationDate"/>
    /// reads the transcript's date, which says how old the numbers are.
    /// </summary>
    public static ClaudeCodeReading? FromStatusLine(byte[] data, DateTimeOffset now, Func<string, DateTimeOffset?> modificationDate)
    {
        var root = Json.Parse(data);
        if (Json.Obj(root, "rate_limits") is not { } limits) return null;
        var transcript = Json.Str(root, "transcript_path") is { } path ? modificationDate(path) : null;
        var seenAt = transcript is { } seen && seen < now ? seen : now;

        Window? Read(string key)
        {
            var entry = Json.Obj(limits, key);
            if (Json.Num(entry, "used_percentage") is not { } percent || !double.IsFinite(percent)) return null;
            if (Json.Num(entry, "resets_at") is not { } resets || !double.IsFinite(resets) || resets <= 0) return null;
            if (resets > 100_000_000_000) resets /= 1000; // milliseconds
            if (resets > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return null;
            return new Window(Math.Max(percent, 0), DateTimeOffset.FromUnixTimeMilliseconds((long)(resets * 1000)), seenAt);
        }
        return new ClaudeCodeReading(Read("five_hour"), Read("seven_day"));
    }

    // The saved file: {"session": {"percent": 4, "resets_at": 1794540000, "seen_at": 1794503000}, "weekly": {…}}.

    public static ClaudeCodeReading? Decode(byte[] data)
    {
        var root = Json.Parse(data);
        if (root is not JsonObject) return null;
        Window? Read(string key)
        {
            var entry = Json.Obj(root, key);
            if (Json.Num(entry, "percent") is not { } percent || Json.Num(entry, "resets_at") is not { } resets
                || Json.Num(entry, "seen_at") is not { } seen) return null;
            return new Window(percent, FromSeconds(resets), FromSeconds(seen));
        }
        return new ClaudeCodeReading(Read("session"), Read("weekly"));
    }

    public byte[] Encode()
    {
        var root = new JsonObject();
        if (Session is { } session) root["session"] = Node(session);
        if (Weekly is { } weekly) root["weekly"] = Node(weekly);
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static JsonObject Node(Window window) => new()
    {
        ["percent"] = window.Percent,
        ["resets_at"] = window.ResetsAt.ToUnixTimeMilliseconds() / 1000.0,
        ["seen_at"] = window.SeenAt.ToUnixTimeMilliseconds() / 1000.0,
    };

    private static DateTimeOffset FromSeconds(double seconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));

    /// <summary>For <c>--dump</c>: "5 h 4% · 7 d 19%".</summary>
    public string Summary() => string.Join(" · ", new[] { ("5 h", Session), ("7 d", Weekly) }
        .Where(entry => entry.Item2 is not null)
        .Select(entry => $"{entry.Item1} {UsageFormat.Percent(entry.Item2!.Percent)}"));
}

/// <summary>Puts Claude Code's numbers together with the usage endpoint's.</summary>
public static class ClaudeCodeUsage
{
    /// <summary>
    /// The endpoint's snapshot, with Claude Code's session and weekly numbers where they are newer:
    /// a later period, or a higher number in the same one. With no snapshot (the endpoint refused
    /// from the start), Claude Code's two limits alone. Also returns the time of the newest number used.
    /// </summary>
    public static (ProviderSnapshot? Snapshot, DateTimeOffset? Updated) Combine(
        ProviderSnapshot? fetched, DateTimeOffset? fetchedAt, ClaudeCodeReading? reading, DateTimeOffset now)
    {
        if (reading?.Current(now) is not { IsEmpty: false } current) return (fetched, fetchedAt);
        (string Kind, LimitGroup Group, ClaudeCodeReading.Window? Window)[] windows =
        [
            ("session", LimitGroup.Session, current.Session),
            ("weekly_all", LimitGroup.Weekly, current.Weekly),
        ];

        if (fetched is null)
        {
            var only = windows.Where(w => w.Window is not null).Select(w => Item(w.Kind, w.Group, w.Window!)).ToList();
            var seen = windows.Where(w => w.Window is not null).Max(w => w.Window!.SeenAt);
            return (new ProviderSnapshot(ProviderId.Claude, null, only, [], [new SnapshotNote(NoteKind.FromClaudeCode)]), seen);
        }

        var items = fetched.Items.ToList();
        var updated = fetchedAt;
        var fetchedTime = fetchedAt ?? DateTimeOffset.MinValue;
        foreach (var (kind, group, window) in windows)
        {
            if (window is null) continue;
            var index = items.FindIndex(item => item.Kind == kind);
            if (index < 0)
            {
                if (window.SeenAt <= fetchedTime) continue;
                items.Insert(kind == "session" ? 0 : Math.Min(1, items.Count), Item(kind, group, window));
                updated = Later(updated, window.SeenAt);
                continue;
            }
            var shown = items[index];
            var resetsAt = shown.ResetsAt;
            if (resetsAt is { } at && ClaudeCodeReading.SamePeriod(at, window.ResetsAt))
            {
                if (window.Percent <= shown.Percent) continue;
            }
            else if (resetsAt is { } later && later > window.ResetsAt)
            {
                continue;
            }
            else
            {
                // A later period than the endpoint's (or one it gave no date for): only if seen after it.
                if (window.SeenAt <= fetchedTime) continue;
                resetsAt = window.ResetsAt;
            }
            items[index] = shown with
            {
                Percent = window.Percent,
                Severity = SeverityParser.From(null, window.Percent),
                ResetsAt = resetsAt,
            };
            updated = Later(updated, window.SeenAt);
        }
        return (fetched with { Items = items }, updated);
    }

    /// <summary>The same numbers, notes and plan (records compare their lists by reference).</summary>
    public static bool Same(ProviderSnapshot? a, ProviderSnapshot? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.Provider == b.Provider && a.PlanName == b.PlanName
            && a.Items.SequenceEqual(b.Items) && a.Breakdown.SequenceEqual(b.Breakdown) && a.Notes.SequenceEqual(b.Notes));

    private static DateTimeOffset Later(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value > b ? value : b;

    private static LimitItem Item(string kind, LimitGroup group, ClaudeCodeReading.Window window) =>
        new($"claude-code-{kind}", kind, null, window.Percent, SeverityParser.From(null, window.Percent), window.ResetsAt, group);
}
