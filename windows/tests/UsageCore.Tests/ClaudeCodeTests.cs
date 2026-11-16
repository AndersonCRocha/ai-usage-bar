using System.Text;
using System.Text.Json.Nodes;
using AIUsageBar.Core;

namespace UsageCore.Tests;

public sealed class ClaudeCodeTests : IDisposable
{
    /// <summary>2027-01-15 08:00:00 UTC.</summary>
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly DateTimeOffset SessionReset = Now.AddHours(3);
    private static readonly DateTimeOffset WeekReset = Now.AddDays(5);
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ClaudeCodeTests-" + Guid.NewGuid().ToString("N"));

    public ClaudeCodeTests()
    {
        Localization.Current = AppLanguage.Portuguese;
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    private static byte[] StatusLine(double? session = 4, double? weekly = 19, string? transcript = "/tmp/session.jsonl")
    {
        var limits = new JsonObject();
        if (session is { } s) limits["five_hour"] = new JsonObject { ["used_percentage"] = s, ["resets_at"] = SessionReset.ToUnixTimeSeconds() };
        if (weekly is { } w) limits["seven_day"] = new JsonObject { ["used_percentage"] = w, ["resets_at"] = WeekReset.ToUnixTimeSeconds() };
        var root = new JsonObject { ["session_id"] = "abc", ["rate_limits"] = limits };
        if (transcript is not null) root["transcript_path"] = transcript;
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static ClaudeCodeReading.Window Window(double percent, DateTimeOffset resets, DateTimeOffset? seen = null) =>
        new(percent, resets, seen ?? Now);

    // MARK: Reading the status line input

    [Fact]
    public void ReadsTheRateLimitsAndDatesThemByTheTranscript()
    {
        var seen = Now.AddMinutes(-10);
        var reading = ClaudeCodeReading.FromStatusLine(StatusLine(), Now, path => path == "/tmp/session.jsonl" ? seen : null)!;
        Assert.Equal(Window(4, SessionReset, seen), reading.Session);
        Assert.Equal(Window(19, WeekReset, seen), reading.Weekly);
        Assert.Equal(Now, ClaudeCodeReading.FromStatusLine(StatusLine(transcript: null), Now, _ => null)!.Session!.SeenAt);
        Assert.Equal(Now, ClaudeCodeReading.FromStatusLine(StatusLine(), Now, _ => Now.AddMinutes(1))!.Session!.SeenAt);
    }

    [Fact]
    public void MissingLimits()
    {
        var onlyWeek = ClaudeCodeReading.FromStatusLine(StatusLine(session: null), Now, _ => null)!;
        Assert.Null(onlyWeek.Session);
        Assert.Equal(19, onlyWeek.Weekly!.Percent);
        Assert.Null(ClaudeCodeReading.FromStatusLine(Encoding.UTF8.GetBytes("{\"rate_limits\": null}"), Now, _ => null));
        Assert.Null(ClaudeCodeReading.FromStatusLine(Encoding.UTF8.GetBytes("not json"), Now, _ => null));
    }

    [Fact]
    public void SavedFileRoundTrips()
    {
        var reading = new ClaudeCodeReading(Window(4.5, SessionReset), Window(19, WeekReset, Now.AddSeconds(-30)));
        Assert.Equal(reading, ClaudeCodeReading.Decode(reading.Encode()));
        Assert.Equal("5 h 5% · 7 d 19%", reading.Summary());
    }

    // MARK: Many sessions

    [Fact]
    public void LaterPeriodWinsThenTheHigherNumber()
    {
        var old = new ClaudeCodeReading(Window(80, SessionReset.AddHours(-5)), Window(13, WeekReset.AddDays(-3)));
        var fresh = new ClaudeCodeReading(Window(4, SessionReset), Window(19, WeekReset));
        var idle = new ClaudeCodeReading(Window(1, SessionReset), Window(14, WeekReset));
        Assert.Equal(fresh, old.Merged(fresh, Now));
        Assert.Equal(fresh, fresh.Merged(idle, Now));
        Assert.Equal(fresh, idle.Merged(fresh, Now));

        var sameLater = new ClaudeCodeReading(Window(4, SessionReset.AddSeconds(1), Now.AddSeconds(30)));
        Assert.Equal(Now.AddSeconds(30), fresh.Merged(sameLater, Now).Session!.SeenAt);
        Assert.Equal(fresh, fresh.Merged(new ClaudeCodeReading(), Now));
        Assert.Null(fresh.Merged(new ClaudeCodeReading(), SessionReset).Session);
    }

    // MARK: Putting it together with the endpoint

    private static ProviderSnapshot Fetched(double session = 5, double weekly = 19) => new(
        ProviderId.Claude, "Max 5x",
        [
            new LimitItem("0-session-", "session", null, session, Severity.Normal, SessionReset, LimitGroup.Session),
            new LimitItem("1-weekly_all-", "weekly_all", null, weekly, Severity.Normal, WeekReset, LimitGroup.Weekly),
            new LimitItem("2-weekly_scoped-Fable", "weekly_scoped", "Fable", 0, Severity.Normal, WeekReset, LimitGroup.Weekly),
        ],
        [new BreakdownItem("claude_code", "Claude Code", 95)],
        []);

    [Fact]
    public void NothingFromTheEndpoint()
    {
        var seen = Now.AddMinutes(-2);
        var reading = new ClaudeCodeReading(Window(4, SessionReset, seen), Window(19, WeekReset, seen));
        var (snapshot, updated) = ClaudeCodeUsage.Combine(null, null, reading, Now);
        Assert.Equal(["session", "weekly_all"], snapshot!.Items.Select(i => i.Kind));
        Assert.Equal([4.0, 19.0], snapshot.Items.Select(i => i.Percent));
        Assert.Equal([LimitGroup.Session, LimitGroup.Weekly], snapshot.Items.Select(i => i.Group));
        Assert.Equal(NoteKind.FromClaudeCode, Assert.Single(snapshot.Notes).Kind);
        Assert.Null(snapshot.PlanName);
        Assert.Equal(seen, updated);
        Assert.Equal("Números do Claude Code no terminal. Os outros limites voltam quando o servidor responder.", snapshot.Notes[0].Text);
        Assert.Null(ClaudeCodeUsage.Combine(null, null, reading, WeekReset).Snapshot);
    }

    [Fact]
    public void RaisesTheEndpointsNumbersInTheSamePeriod()
    {
        var fetchedAt = Now.AddMinutes(-15);
        var reading = new ClaudeCodeReading(Window(31, SessionReset.AddSeconds(40)), Window(12, WeekReset));
        var (snapshot, updated) = ClaudeCodeUsage.Combine(Fetched(), fetchedAt, reading, Now);
        Assert.Equal([31.0, 19.0, 0.0], snapshot!.Items.Select(i => i.Percent));
        Assert.Equal(SessionReset, snapshot.Items[0].ResetsAt);
        Assert.Equal("0-session-", snapshot.Items[0].Id);
        Assert.Single(snapshot.Breakdown);
        Assert.Equal("Max 5x", snapshot.PlanName);
        Assert.Equal(Now, updated);

        var lower = new ClaudeCodeReading(Window(4, SessionReset));
        var unchanged = ClaudeCodeUsage.Combine(Fetched(), fetchedAt, lower, Now);
        Assert.True(ClaudeCodeUsage.Same(Fetched(), unchanged.Snapshot));
        Assert.Equal(fetchedAt, unchanged.Updated);
    }

    [Fact]
    public void ANewPeriodCountsOnlyWhenSeenAfterTheEndpoint()
    {
        var fetchedAt = Now.AddHours(-6);
        var nextSession = SessionReset.AddHours(5);
        var snapshot = ClaudeCodeUsage.Combine(Fetched(session: 90), fetchedAt, new ClaudeCodeReading(Window(2, nextSession)), Now).Snapshot!;
        Assert.Equal(2, snapshot.Items[0].Percent);
        Assert.Equal(nextSession, snapshot.Items[0].ResetsAt);
        Assert.Equal(Severity.Normal, snapshot.Items[0].Severity);

        var older = new ClaudeCodeReading(Window(2, nextSession, fetchedAt.AddMinutes(-1)));
        Assert.True(ClaudeCodeUsage.Same(Fetched(session: 90), ClaudeCodeUsage.Combine(Fetched(session: 90), fetchedAt, older, Now).Snapshot));
        var earlier = new ClaudeCodeReading(Window(70, SessionReset.AddHours(-1)));
        Assert.True(ClaudeCodeUsage.Same(Fetched(), ClaudeCodeUsage.Combine(Fetched(), fetchedAt, earlier, Now).Snapshot));
    }

    // MARK: Editing settings.json

    [Fact]
    public void EditsOnlyTheStatusLine()
    {
        const string text = "{\n  \"model\": \"opus\",\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"~/.claude/line.sh\"\n  },\n  \"theme\": \"dark\"\n}\n";
        Assert.Equal("{\n    \"type\": \"command\",\n    \"command\": \"~/.claude/line.sh\"\n  }", JsonText.ValueOf("statusLine", text));

        var replaced = JsonText.Setting("statusLine", "{\"x\":1}", text);
        Assert.Equal("{\n  \"model\": \"opus\",\n  \"statusLine\": {\"x\":1},\n  \"theme\": \"dark\"\n}\n", replaced);
        Assert.Equal("{\n  \"model\": \"opus\",\n  \"theme\": \"dark\"\n}\n", JsonText.Setting("statusLine", null, text));
        Assert.Equal("{\n  \"statusLine\": {\"x\":1},\n  \"theme\": \"dark\"\n}\n", JsonText.Setting("model", null, replaced));
        Assert.Equal("{\n}", JsonText.Setting("a", null, "{\"a\": [1, {\"b\": \"}\"}]}"));
        Assert.Equal("{\n    \"model\": \"opus\",\n    \"statusLine\": {}\n}", JsonText.Setting("statusLine", "{}", "{\n    \"model\": \"opus\"\n}"));
        Assert.Equal("{\n  \"statusLine\": {}\n}", JsonText.Setting("statusLine", "{}", "{}"));
        Assert.Equal("{\n  \"statusLine\": {}\n}\n", JsonText.Setting("statusLine", "{}", null));
    }

    private static readonly ClaudeCodeShell Unix = new(ClaudeCodeShell.Kind.Posix, "/bin/sh", WindowsPaths: false);

    private ClaudeCodeBridge Bridge(ClaudeCodeShell? shell = null) =>
        new(Path.Combine(folder, "app"), Path.Combine(folder, "claude", "settings.json"), shell ?? Unix);

    private const string Executable = "/opt/AI Usage Bar/AIUsageBar";

    [Fact]
    public void InstallsAndPutsTheUsersStatusLineBack()
    {
        var bridge = Bridge();
        Directory.CreateDirectory(Path.GetDirectoryName(bridge.SettingsPath)!);
        const string original = "{\n  \"model\": \"opus\",\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"~/.claude/line.sh\",\n    \"padding\": 2\n  }\n}\n";
        File.WriteAllText(bridge.SettingsPath, original);

        Assert.False(bridge.IsInstalled());
        bridge.Install(Executable);
        Assert.True(bridge.IsInstalled());
        Assert.Equal("~/.claude/line.sh", bridge.PreviousCommand());
        var settings = JsonNode.Parse(File.ReadAllText(bridge.SettingsPath))!;
        Assert.Equal("'/opt/AI Usage Bar/AIUsageBar' --statusline", (string?)settings["statusLine"]!["command"]);
        Assert.Equal(2, (int?)settings["statusLine"]!["padding"]);
        Assert.Equal("opus", (string?)settings["model"]);

        bridge.Install(Executable);
        bridge.Refresh("/home/me/AIUsageBar");
        Assert.Contains("/home/me/AIUsageBar", File.ReadAllText(bridge.SettingsPath));
        Assert.Equal("~/.claude/line.sh", bridge.PreviousCommand());

        bridge.Uninstall();
        Assert.Equal(original, File.ReadAllText(bridge.SettingsPath));
        Assert.False(bridge.IsInstalled());
    }

    [Fact]
    public void InstallsWhereThereWasNothing()
    {
        var bridge = Bridge();
        bridge.Install(Executable);
        Assert.Null(bridge.PreviousCommand());
        Assert.True(bridge.IsInstalled());
        bridge.Uninstall();
        Assert.Equal("{\n}\n", File.ReadAllText(bridge.SettingsPath));
        bridge.Refresh(Executable);
        Assert.False(bridge.IsInstalled());
    }

    [Fact]
    public void LeavesBrokenSettingsAlone()
    {
        var bridge = Bridge();
        Directory.CreateDirectory(Path.GetDirectoryName(bridge.SettingsPath)!);
        File.WriteAllText(bridge.SettingsPath, "{ \"model\": ");
        var error = Assert.Throws<ClaudeCodeSetupException>(() => bridge.Install(Executable));
        Assert.StartsWith("Não consegui ler o settings.json", error.Message);
        Assert.Equal("{ \"model\": ", File.ReadAllText(bridge.SettingsPath));
        Assert.False(bridge.IsInstalled());
    }

    [Fact]
    public void QuotesTheCommandForEachShell()
    {
        const string exe = @"C:\Users\Ana Maria\AppData\Local\Programs\AI Usage Bar\AIUsageBar.exe";
        var bash = new ClaudeCodeShell(ClaudeCodeShell.Kind.Posix, @"C:\Program Files\Git\bin\bash.exe", WindowsPaths: true);
        Assert.Equal("'C:/Users/Ana Maria/AppData/Local/Programs/AI Usage Bar/AIUsageBar.exe' --statusline", bash.Command(exe));
        var powershell = new ClaudeCodeShell(ClaudeCodeShell.Kind.PowerShell, "powershell.exe", WindowsPaths: true);
        Assert.Equal($"& '{exe}' --statusline", powershell.Command(exe));
        Assert.Equal("& 'C:\\it''s\\AIUsageBar.exe' --statusline", powershell.Command(@"C:\it's\AIUsageBar.exe"));
        Assert.Equal("'/a/it'\\''s/AIUsageBar' --statusline", Unix.Command("/a/it's/AIUsageBar"));
        Assert.True(ClaudeCodeBridge.IsOurs(powershell.Command(exe)));
        Assert.False(ClaudeCodeBridge.IsOurs("~/.claude/line.sh"));
    }

    [Fact]
    public void FindsGitBashLikeClaudeCode()
    {
        static Func<string, string?> Env(params (string Name, string Value)[] values) =>
            name => values.FirstOrDefault(v => v.Name == name).Value;
        var files = new HashSet<string> { @"D:\Git\cmd\git.exe", @"D:\Git\cmd\..\bin\bash.exe", @"C:\Program Files\Git\bin\bash.exe" };

        Assert.Equal(@"E:\bash.exe", ClaudeCodeShell.FindGitBash(Env(("CLAUDE_CODE_GIT_BASH_PATH", @"E:\bash.exe")), _ => true));
        Assert.Equal(@"D:\Git\cmd\..\bin\bash.exe", ClaudeCodeShell.FindGitBash(Env(("PATH", @"C:\Windows;D:\Git\cmd\"), ("ProgramFiles", @"C:\Program Files")), files.Contains));
        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", ClaudeCodeShell.FindGitBash(Env(("PATH", @"C:\Windows"), ("ProgramFiles", @"C:\Program Files")), files.Contains));
        var none = ClaudeCodeShell.ForWindows(Env(("PATH", @"C:\Windows")), _ => false);
        Assert.Equal(ClaudeCodeShell.Kind.PowerShell, none.Type);
    }

    [Fact]
    public void TheCommandSavesTheNumbersAndRunsTheUsersLine()
    {
        var bridge = Bridge(ClaudeCodeShell.Detect());
        var output = Path.Combine(folder, "seen.json").Replace('\\', '/');
        Directory.CreateDirectory(Path.GetDirectoryName(bridge.SettingsPath)!);
        var settings = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = $"cat > '{output}'; echo hi; exit 3" } };
        File.WriteAllText(bridge.SettingsPath, settings.ToJsonString());
        bridge.Install(Executable);

        var input = StatusLine(transcript: null);
        using var stdout = new MemoryStream();
        Assert.Equal(3, bridge.RunStatusLine(input, stdout, Now));
        Assert.Equal(input, File.ReadAllBytes(output));
        Assert.Equal("hi", Encoding.UTF8.GetString(stdout.ToArray()).Trim());
        Assert.Equal(4, bridge.LoadReading()!.Session!.Percent);

        bridge.RunStatusLine(StatusLine(session: 1, weekly: null, transcript: null), Stream.Null, Now);
        Assert.Equal(4, bridge.LoadReading()!.Session!.Percent);
        Assert.Equal(19, bridge.LoadReading()!.Weekly!.Percent);

        bridge.Uninstall();
        Assert.Null(bridge.LoadReading());
        Assert.Equal(0, bridge.RunStatusLine(input, Stream.Null, Now));
    }
}
