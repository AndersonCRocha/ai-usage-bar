using System.Text;
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using SkiaSharp;

namespace UsageCore.Tests;

public class ServiceStatusTests
{
    public ServiceStatusTests() => Localization.Current = AppLanguage.English;

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static StatusPage Page(ProviderId provider) => provider.StatusPage()!;

    /// <summary>The shape of status.claude.com's summary.json, cut down.</summary>
    private const string ClaudeDegraded = """
        {
          "page": { "id": "tymt9n04zgry", "name": "Claude", "url": "https://status.claude.com" },
          "status": { "indicator": "major", "description": "Partial System Outage" },
          "components": [
            { "id": "a", "name": "claude.ai", "status": "degraded_performance", "group": false },
            { "id": "b", "name": "Claude Console (platform.claude.com)", "status": "operational", "group": false },
            { "id": "c", "name": "Claude API (api.anthropic.com)", "status": "operational", "group": false },
            { "id": "d", "name": "Claude Code", "status": "operational", "group": false },
            { "id": "e", "name": "Claude for Government", "status": "major_outage", "group": true },
            "not a component"
          ],
          "incidents": [
            {
              "name": "Elevated errors on claude.ai",
              "status": "investigating",
              "impact": "major",
              "components": [ { "name": "claude.ai", "status": "degraded_performance" } ]
            },
            {
              "name": "Console login issues",
              "status": "identified",
              "impact": "critical",
              "components": [ { "name": "Claude Console (platform.claude.com)", "status": "major_outage" } ]
            },
            {
              "name": "Yesterday's API errors",
              "status": "resolved",
              "impact": "critical",
              "components": [ { "name": "Claude API (api.anthropic.com)" } ]
            },
            42
          ]
        }
        """;

    private const string ClaudeOperational = """
        {
          "status": { "indicator": "none", "description": "All Systems Operational" },
          "components": [
            { "name": "claude.ai", "status": "operational" },
            { "name": "Claude API (api.anthropic.com)", "status": "operational" },
            { "name": "Claude Code", "status": "operational" }
          ],
          "incidents": []
        }
        """;

    [Fact]
    public void ReadsADegradedComponentAndItsOpenIncident()
    {
        var status = ServiceStatus.Statuspage(Bytes(ClaudeDegraded), null, Page(ProviderId.Claude));

        // Degraded component, raised by the "major" incident on it.
        Assert.Equal(StatusLevel.PartialOutage, status.Level);
        Assert.Equal(["claude.ai"], status.Components);
        // Only the open incident on a part Claude relies on: not the Console one, not the resolved one.
        Assert.Equal(["Elevated errors on claude.ai"], status.Incidents);
        Assert.Equal(new Uri("https://status.claude.com"), status.PageUrl);
        Assert.True(status.IsProblem);
        Assert.True(status.IsNotice);
        Assert.Equal(Severity.Warning, status.Severity);
        Assert.Equal("Claude is having issues", status.Title(ProviderId.Claude));
        Assert.Equal(["Elevated errors on claude.ai", "Affected: claude.ai"], status.DetailLines);
    }

    [Fact]
    public void AnOperationalPageIsNothingToShow()
    {
        var status = ServiceStatus.Statuspage(Bytes(ClaudeOperational), null, Page(ProviderId.Claude));
        Assert.Equal(StatusLevel.Operational, status.Level);
        Assert.Empty(status.Components);
        Assert.Empty(status.Incidents);
        Assert.False(status.IsProblem);
        Assert.False(status.IsNotice);
        Assert.Empty(status.DetailLines);
    }

    [Fact]
    public void IgnoresAnIncidentOnAnotherComponent()
    {
        var json = """
            {
              "status": { "indicator": "critical" },
              "components": [
                { "name": "claude.ai", "status": "operational" },
                { "name": "Claude Console (platform.claude.com)", "status": "major_outage" }
              ],
              "incidents": [
                { "name": "Console down", "status": "investigating", "impact": "critical",
                  "components": [ { "name": "Claude Console (platform.claude.com)" } ] },
                { "name": "No components listed", "status": "investigating", "impact": "critical" }
              ]
            }
            """;
        var status = ServiceStatus.Statuspage(Bytes(json), null, Page(ProviderId.Claude));
        Assert.Equal(StatusLevel.Operational, status.Level);
        Assert.Empty(status.Incidents);
        Assert.Empty(status.Components);
    }

    /// <summary>incident.io (status.openai.com): the summary lists only the first 25 components.</summary>
    private const string OpenAiSummary = """
        {
          "status": { "indicator": "minor" },
          "components": [
            { "name": "APIs", "status": "operational" },
            { "name": "ChatGPT", "status": "operational" }
          ],
          "incidents": [
            { "name": "Codex CLI errors", "status": "monitoring", "impact": "minor",
              "components": [ { "name": "CLI", "status": "partial_outage" } ] }
          ]
        }
        """;

    private const string OpenAiComponents = """
        {
          "components": [
            { "name": "APIs", "status": "operational" },
            { "name": "ChatGPT", "status": "operational" },
            { "name": "Codex", "status": "full_outage", "group": true },
            { "name": "Codex Web", "status": "operational" },
            { "name": "CLI", "status": "partial_outage" },
            { "name": "VS Code extension", "status": "operational" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheFullComponentListWhenThereIsOne()
    {
        var page = Page(ProviderId.Codex);
        var status = ServiceStatus.Statuspage(Bytes(OpenAiSummary), Bytes(OpenAiComponents), page);
        // The "Codex" group is skipped: only its parts count.
        Assert.Equal(StatusLevel.PartialOutage, status.Level);
        Assert.Equal(["CLI"], status.Components);
        Assert.Equal(["Codex CLI errors"], status.Incidents);
        Assert.Equal("Codex is having issues", status.Title(ProviderId.Codex));

        // Without the full list (or with one that is not JSON), the summary's components are used: none of them is
        // a Codex part, so the whole page's indicator decides.
        Assert.Equal(StatusLevel.Degraded, ServiceStatus.Statuspage(Bytes(OpenAiSummary), null, page).Level);
        Assert.Equal(StatusLevel.Degraded, ServiceStatus.Statuspage(Bytes(OpenAiSummary), Bytes("<html>"), page).Level);
    }

    [Fact]
    public void FallsBackToThePageIndicatorWhenTheComponentsWereRenamed()
    {
        var json = """
            {
              "status": { "indicator": "major" },
              "components": [
                { "name": "Claude Web", "status": "partial_outage" },
                { "name": "Claude Web", "status": "partial_outage" },
                { "name": "Anthropic API", "status": "operational" },
                { "name": "Status group", "status": "major_outage", "group": true }
              ],
              "incidents": [
                { "name": "Errors on Claude Web", "status": "investigating", "impact": "minor", "components": [] },
                { "name": "", "status": "investigating" },
                { "name": "Old one", "status": "postmortem" }
              ]
            }
            """;
        var status = ServiceStatus.Statuspage(Bytes(json), null, Page(ProviderId.Claude));
        Assert.Equal(StatusLevel.PartialOutage, status.Level);
        Assert.Equal(["Claude Web"], status.Components);
        Assert.Equal(["Errors on Claude Web"], status.Incidents);
    }

    [Fact]
    public void MaintenanceIsANoticeButNotAProblem()
    {
        var json = """
            {
              "status": { "indicator": "maintenance" },
              "components": [ { "name": "Copilot", "status": "under_maintenance" }, { "name": "Git Operations", "status": "operational" } ],
              "incidents": []
            }
            """;
        var status = ServiceStatus.Statuspage(Bytes(json), null, Page(ProviderId.Copilot));
        Assert.Equal(StatusLevel.Maintenance, status.Level);
        Assert.False(status.IsProblem);
        Assert.True(status.IsNotice);
        Assert.Equal(["Copilot"], status.Components);
        Assert.Equal("GitHub Copilot is under maintenance", status.Title(ProviderId.Copilot));
        Localization.Current = AppLanguage.Portuguese;
        Assert.Equal("GitHub Copilot em manutenção", status.Title(ProviderId.Copilot));
        Assert.Equal(["Afetado: Copilot"], status.DetailLines);
        Assert.Equal("Ver página de status", ServiceStatus.LinkTitle);
    }

    [Fact]
    public void ReadsGoogleCloudIncidents()
    {
        var json = """
            [
              {
                "id": "open-gemini",
                "external_desc": "  Gemini Code Assist users may see elevated error rates  ",
                "begin": "2026-11-16T10:00:00+00:00",
                "end": "",
                "status_impact": "SERVICE_DISRUPTION",
                "affected_products": [
                  { "title": "Gemini Code Assist", "id": "deUeOEPYanfJ9w8cpyBJ" },
                  { "title": "Vertex Gemini API", "id": "Z0FZJAMvEB4j3NbCJs6B" }
                ]
              },
              {
                "id": "ended-gemini",
                "external_desc": "Gemini Code Assist was down",
                "end": "2026-11-15T12:00:00+00:00",
                "status_impact": "SERVICE_OUTAGE",
                "affected_products": [ { "title": "Gemini Code Assist", "id": "deUeOEPYanfJ9w8cpyBJ" } ]
              },
              {
                "id": "open-storage",
                "external_desc": "Cloud Storage outage",
                "status_impact": "SERVICE_OUTAGE",
                "affected_products": [ { "title": "Cloud Storage", "id": "UwaYoXQ5bHYHG6EdiPB8" } ]
              },
              "malformed"
            ]
            """;
        var status = ServiceStatus.GoogleCloud(Bytes(json), Page(ProviderId.Gemini));
        Assert.Equal(StatusLevel.PartialOutage, status.Level);
        Assert.Equal(["Gemini Code Assist users may see elevated error rates"], status.Incidents);
        Assert.Equal(["Gemini Code Assist"], status.Components);
        Assert.Equal(new Uri("https://status.cloud.google.com"), status.PageUrl);

        Assert.Equal(StatusLevel.Operational, ServiceStatus.GoogleCloud(Bytes("[]"), Page(ProviderId.Gemini)).Level);
    }

    [Fact]
    public void AnUnreadableFeedIsADecodingError()
    {
        Assert.Equal(UsageErrorKind.Decoding,
            Assert.Throws<UsageException>(() => ServiceStatus.Statuspage(Bytes("<html>"), null, Page(ProviderId.Claude))).Kind);
        Assert.Equal(UsageErrorKind.Decoding,
            Assert.Throws<UsageException>(() => ServiceStatus.Statuspage(Bytes("[]"), null, Page(ProviderId.Claude))).Kind);
        Assert.Equal(UsageErrorKind.Decoding,
            Assert.Throws<UsageException>(() => ServiceStatus.GoogleCloud(Bytes("{}"), Page(ProviderId.Gemini))).Kind);
        // An object with nothing in it is a page with nothing to say.
        Assert.Equal(StatusLevel.Operational, ServiceStatus.Statuspage(Bytes("{}"), null, Page(ProviderId.Claude)).Level);
    }

    [Fact]
    public void StatusPagesPerAgent()
    {
        Assert.Null(ProviderId.Cursor.StatusPage());
        Assert.Null(ProviderId.GrokBot.StatusPage());
        Assert.Equal(new Uri("https://status.claude.com"), ProviderId.Claude.StatusPage()!.PageUrl);
        Assert.Equal(
            new StatusFeed.Statuspage(new Uri("https://status.openai.com/api/v2/summary.json"), new Uri("https://status.openai.com/api/v2/components.json")),
            ProviderId.Codex.StatusPage()!.Feed);
        Assert.IsType<StatusFeed.Statuspage>(ProviderId.Copilot.StatusPage()!.Feed);
        Assert.IsType<StatusFeed.GoogleCloud>(ProviderId.Gemini.StatusPage()!.Feed);
    }

    [Theory]
    [InlineData("operational", StatusLevel.Operational)]
    [InlineData("under_maintenance", StatusLevel.Maintenance)]
    [InlineData("degraded_performance", StatusLevel.Degraded)]
    [InlineData("partial_outage", StatusLevel.PartialOutage)]
    [InlineData("major_outage", StatusLevel.MajorOutage)]
    [InlineData("full_outage", StatusLevel.MajorOutage)]
    [InlineData(null, StatusLevel.Operational)]
    public void ComponentLevels(string? status, StatusLevel level) => Assert.Equal(level, StatusLevels.Component(status));

    [Fact]
    public void ImpactsAndIndicators()
    {
        Assert.Equal(StatusLevel.Degraded, StatusLevels.Impact("none"));
        Assert.Equal(StatusLevel.Degraded, StatusLevels.Impact(null));
        Assert.Equal(StatusLevel.PartialOutage, StatusLevels.Impact("major"));
        Assert.Equal(StatusLevel.MajorOutage, StatusLevels.Impact("Critical"));
        Assert.Equal(StatusLevel.Operational, StatusLevels.Indicator("none"));
        Assert.Equal(StatusLevel.Degraded, StatusLevels.Indicator("minor"));
        Assert.Equal(StatusLevel.Maintenance, StatusLevels.Indicator("maintenance"));
        Assert.Equal(StatusLevel.MajorOutage, StatusLevels.GoogleImpact("service_outage"));
        Assert.Equal(StatusLevel.Degraded, StatusLevels.GoogleImpact("SERVICE_INFORMATION"));
    }

    [Fact]
    public void TitlesAndSeverity()
    {
        var down = new ServiceStatus(StatusLevel.MajorOutage, [], ["Outage"], new Uri("https://status.claude.com"));
        Assert.Equal("Claude is down", down.Title(ProviderId.Claude));
        Assert.Equal(Severity.Critical, down.Severity);
        Assert.Equal("Gemini is operating normally", (down with { Level = StatusLevel.Operational }).Title(ProviderId.Gemini));
        Localization.Current = AppLanguage.Portuguese;
        Assert.Equal("Claude fora do ar", down.Title(ProviderId.Claude));
        Assert.Equal("Codex com instabilidade", (down with { Level = StatusLevel.Degraded }).Title(ProviderId.Codex));
        // Same content, same status: an unchanged page does not redraw anything.
        Assert.Equal(down, new ServiceStatus(StatusLevel.MajorOutage, [], ["Outage"], new Uri("https://status.claude.com")));
        Assert.NotEqual(down, down with { Incidents = ["Another"] });
    }

    [Fact]
    public void DumpLineAndPreviewStatuses()
    {
        var status = ServiceStatus.Statuspage(Bytes(ClaudeDegraded), null, Page(ProviderId.Claude));
        Assert.Equal("Claude is having issues — Elevated errors on claude.ai; Affected: claude.ai", status.Summary(ProviderId.Claude));

        var preview = ServiceStatus.Preview(ProviderId.Codex, "partial")!;
        Assert.Equal(StatusLevel.PartialOutage, preview.Level);
        Assert.Equal(["Elevated error rates"], preview.Incidents);
        Assert.Empty(preview.Components);
        Assert.Equal(new Uri("https://status.openai.com"), preview.PageUrl);
        // No status page of its own: Claude's stands in.
        Assert.Equal(new Uri("https://status.claude.com"), ServiceStatus.Preview(ProviderId.Cursor, "major")!.PageUrl);
        Assert.Equal(StatusLevel.Maintenance, ServiceStatus.Preview(ProviderId.Claude, "maintenance")!.Level);
        Assert.Equal(StatusLevel.Degraded, ServiceStatus.Preview(ProviderId.Claude, "degraded")!.Level);
        Assert.Null(ServiceStatus.Preview(ProviderId.Claude, "fine"));
    }

    // MARK: When to read

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsAPageAtMostEveryTwoMinutesOrFifteenSecondsWhenAsked()
    {
        Assert.True(StatusSchedule.IsDue(null, Now));
        Assert.False(StatusSchedule.IsDue(Now.AddSeconds(-119), Now));
        Assert.True(StatusSchedule.IsDue(Now.AddSeconds(-120), Now));
        Assert.False(StatusSchedule.IsDue(Now.AddSeconds(-14), Now, force: true));
        Assert.True(StatusSchedule.IsDue(Now.AddSeconds(-15), Now, force: true));
    }

    [Fact]
    public void AFailedReadKeepsTheLastStatusForHalfAnHour()
    {
        var status = new ServiceStatus(StatusLevel.Degraded, [], [], new Uri("https://status.claude.com"));
        Assert.Same(status, StatusSchedule.AfterFailure(status, Now.AddMinutes(-29), Now));
        Assert.Null(StatusSchedule.AfterFailure(status, Now.AddMinutes(-31), Now));
        Assert.Null(StatusSchedule.AfterFailure(status, null, Now));
    }

    // MARK: Tray and tabs

    private static ProviderSnapshot Claude() =>
        ClaudeProvider.SnapshotFromResponse(Bytes(ClaudeTests.Sample), "Max 5x")!;

    private static readonly ServiceStatus Degraded =
        new(StatusLevel.Degraded, ["claude.ai"], ["Elevated error rates"], new Uri("https://status.claude.com"));

    [Fact]
    public void AProblemPutsABadgeAndALineOnTheTrayIcon()
    {
        var segment = TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, Claude(), null, status: Degraded)!;
        Assert.Equal(Severity.Warning, segment.Alert);
        Assert.Equal("42", segment.Number);
        Assert.StartsWith("Claude\n⚠ Claude is having issues\n", segment.Tooltip);

        var down = TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, null, "boom", status: Degraded with { Level = StatusLevel.MajorOutage })!;
        Assert.Equal(Severity.Critical, down.Alert);
        Assert.Equal("!", down.Number);
        Assert.Equal("⚠ Claude is down\nClaude: boom", down.Tooltip);

        // Maintenance stays in the popup; a hidden icon stays hidden.
        var maintenance = TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, Claude(), null, status: Degraded with { Level = StatusLevel.Maintenance })!;
        Assert.Null(maintenance.Alert);
        Assert.DoesNotContain("⚠", maintenance.Tooltip);
        Assert.Null(TraySegment.Make(ProviderId.Claude, TrayDisplay.Hidden, Claude(), null, status: Degraded));
    }

    [Fact]
    public void AnOutageMarksTheTab()
    {
        var summary = TabSummary.Make(Claude(), hasError: true);
        Assert.True(summary.IsWarning);
        Assert.Equal("91%", summary.Text);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(32, true)]
    public void DrawsTheBadgeOnTheRingsTopRight(int size, bool colorAlerts)
    {
        var plain = TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, Claude(), null)!;
        var alert = plain with { Alert = Severity.Critical };
        using var without = TrayIconRenderer.Render(plain, ProviderId.Claude, TrayStyle.Percent, null, colorAlerts, darkTaskbar: true, size);
        using var with = TrayIconRenderer.Render(alert, ProviderId.Claude, TrayStyle.Percent, null, colorAlerts, darkTaskbar: true, size);

        // The badge, left of its "!": red with alert colors on, the taskbar's white otherwise.
        var radius = size * 0.2f;
        var badge = with.GetPixel((int)(size - radius * 1.6f), (int)radius);
        Assert.Equal(colorAlerts ? TrayIconRenderer.Red : SKColors.White, badge);
        // The "!" is cut out where there is room for it (32 px), and left out on a 16 px icon.
        Assert.Equal(size >= 32 ? 0 : 255, with.GetPixel((int)(size - radius), (int)radius).Alpha);
        // The bottom left, far from the badge, does not change.
        Assert.Equal(without.GetPixel(size / 5, size * 4 / 5), with.GetPixel(size / 5, size * 4 / 5));
    }
}
