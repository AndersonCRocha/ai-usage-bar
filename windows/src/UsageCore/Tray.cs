using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

/// <summary>How each service's tray icon is drawn.</summary>
public enum TrayStyle
{
    /// <summary>The service's glyph inside a ring that fills with usage.</summary>
    Icon,
    /// <summary>The percentage inside the ring.</summary>
    Percent,
}

/// <summary>
/// What a service's tray icon tracks. Windows tray icons are 16–32 px
/// squares, too small for two stacked numbers, so there is no
/// "session + weekly" here: the popup shows everything.
/// </summary>
public enum TrayDisplay { Session, Weekly, Hidden }

public static class TrayOptions
{
    public static IReadOnlyList<TrayDisplay> For(ProviderId provider) => provider.Descriptor().TrayOptions;

    public static TrayDisplay Default(ProviderId provider) => provider.Descriptor().DefaultTray;

    public static string Label(this TrayDisplay display, ProviderId provider) => display switch
    {
        TrayDisplay.Session => T("Sessão atual", "Current session"),
        TrayDisplay.Weekly => provider.Descriptor().WeeklyLabel(),
        _ => T("Não mostrar", "Hide"),
    };

    public static string Label(this TrayStyle style) => style == TrayStyle.Icon
        ? T("Ícone da IA com anel", "AI icon with ring")
        : T("Número no anel", "Number in ring");
}

/// <summary>
/// One service's tray icon: a ring and, depending on the style, a glyph or a number.
/// <paramref name="Alert"/>: the service's status page reports a problem, drawn as a badge on the ring.
/// </summary>
public sealed record TraySegment(ProviderId Provider, double? RingPercent, Severity Severity, string Number, string Tooltip, Severity? Alert = null)
{
    /// <summary>Windows truncates tray tooltips at 127 characters.</summary>
    public const int TooltipLimit = 127;

    /// <summary>
    /// <paramref name="isNotice"/>: <paramref name="error"/> is the "too many refreshes" notice, not a warning.
    /// <paramref name="status"/>: what the service's status page says; a problem there adds the badge and a tooltip line.
    /// </summary>
    public static TraySegment? Make(
        ProviderId provider, TrayDisplay display, ProviderSnapshot? snapshot, string? error, bool isNotice = false, ServiceStatus? status = null)
    {
        if (display == TrayDisplay.Hidden) return null;

        var outage = status is { IsProblem: true } ? status : null;
        var items = snapshot?.Items ?? [];
        if (items.Count == 0)
        {
            var text = error ?? T("Carregando…", "Loading…");
            // The outage first: a long error would push it past the 127-character limit.
            var tooltip = (outage is null ? "" : "⚠ " + outage.Title(provider) + "\n") + $"{provider.DisplayName()}: {text}";
            return new TraySegment(provider, null, Severity.Normal, error is null || isNotice ? "–" : "!", Clip(tooltip), outage?.Severity);
        }

        var session = items.FirstOrDefault(i => i.Group == LimitGroup.Session);
        var weekly = items.FirstOrDefault(i => i.Kind == "weekly_all") ?? items.FirstOrDefault(i => i.Group == LimitGroup.Weekly);
        var shown = (display == TrayDisplay.Session ? session ?? weekly : weekly ?? session)
            ?? items.MaxBy(i => i.Percent)!;

        var lines = new List<string> { provider.DisplayName() };
        // Right under the name: the 127-character limit cuts the end of a long tooltip.
        if (outage is not null) lines.Add("⚠ " + outage.Title(provider));
        lines.AddRange(items.Select(i => $"{i.Title}: {UsageFormat.Percent(i.Percent)}"));
        if (error is not null) lines.Add((isNotice ? "ⓘ " : "⚠ ") + error);
        return new TraySegment(provider, shown.Percent, shown.Severity, FormatNumber(shown.Percent), Clip(string.Join('\n', lines)), outage?.Severity);
    }

    /// <summary>"88", "&lt;1", "100": the percentage without the sign, to fit the ring.</summary>
    public static string FormatNumber(double percent) => UsageFormat.Percent(percent).TrimEnd('%');

    private static string Clip(string text) => text.Length <= TooltipLimit ? text : text[..(TooltipLimit - 1)] + "…";
}
