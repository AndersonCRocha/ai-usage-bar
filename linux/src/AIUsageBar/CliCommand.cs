using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using Avalonia;
using Avalonia.Headless;

namespace AIUsageBar;

/// <summary>
/// Modes for debugging and CI:
/// <code>
/// AIUsageBar --dump [--raw]
/// AIUsageBar --snapshot out.png [--fixture claude.json] [--codex-fixture codex.json] [--copilot-fixture copilot.json] [--cursor-fixture cursor.json] [--gemini-fixture gemini.json] [--grok-fixture grok.json] [--dark] [--lang en|pt] [--tab claude|copilot|cursor|grokBot|agents] [--first-run] [--update-available 9.9.9] [--tab-limit claude=weekly_all] [--layout tabs|list] [--no-tray-notice] [--service-status claude=maintenance|degraded|partial|major]
/// AIUsageBar --install-update [--no-relaunch] [--as-version 0.0.1]
/// AIUsageBar --statusline | --install-statusline | --uninstall-statusline   (Claude Code's status line)
/// AIUsageBar --demo [fixtures as above] [--dark] [--lang en|pt]
/// </code>
/// <c>--snapshot</c> renders without a display and also writes <c>out-tray.png</c> with every tray icon combination.
/// <c>--service-status</c> (repeatable, also with <c>--demo</c>) pretends the agent's status page reports that, to see the
/// banner and the tray badge without a real outage.
/// <c>--install-update</c> runs the in-app update from the command line (CI uses it with <c>--as-version</c>).
/// <c>--demo</c> runs the real tray and popup on the fixtures, with no logins read and no network (handled by <see cref="App"/>).
/// </summary>
internal sealed class CliCommand
{
    private readonly string[] args;

    private CliCommand(string[] args) => this.args = args;

    public static CliCommand? TryParse(string[] args) =>
        args.Contains("--dump") || args.Contains("--snapshot") || args.Contains("--install-update")
        || args.Contains("--install-statusline") || args.Contains("--uninstall-statusline") ? new CliCommand(args) : null;

    private string? Option(string name) => Option(args, name);

    internal static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public int Run()
    {
        try
        {
            if (Localization.LanguageFromCode(Option("--lang")) is { } language) Localization.Current = language;
            if (args.Contains("--install-update")) return InstallUpdateAsync().GetAwaiter().GetResult();
            if (args.Contains("--install-statusline") || args.Contains("--uninstall-statusline")) return SetUpStatusLine();
            return args.Contains("--dump") ? DumpAsync().GetAwaiter().GetResult() : Snapshot();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

        /// <summary>Claude Code's status line files: the app's folder and Claude Code's <c>settings.json</c>.</summary>
    internal static ClaudeCodeBridge ClaudeCode() =>
        new(AppSettings.Directory, ClaudeCodeBridge.DefaultSettingsPath(), ClaudeCodeShell.Detect());

    /// <summary>
    /// <c>--statusline</c>: run by Claude Code after every response, with its status line JSON on stdin.
    /// Keeps the Claude numbers and runs the user's own status line, if there was one.
    /// </summary>
    internal static int RunStatusLine()
    {
        using var buffer = new MemoryStream();
        using (var stdin = Console.OpenStandardInput()) stdin.CopyTo(buffer);
        using var stdout = Console.OpenStandardOutput();
        return ClaudeCode().RunStatusLine(buffer.ToArray(), stdout, DateTimeOffset.Now);
    }

    /// <summary><c>--install-statusline</c> and <c>--uninstall-statusline</c>: the ⚙ → Claude switch from a terminal.</summary>
    private int SetUpStatusLine()
    {
        var bridge = ClaudeCode();
        try
        {
            if (args.Contains("--install-statusline"))
            {
                if (Launcher.Path is not { } path) return 1;
                bridge.Install(path);
                Console.WriteLine($"Claude Code's status line now runs AI Usage Bar: {bridge.SettingsPath}");
            }
            else
            {
                bridge.Uninstall();
                Console.WriteLine($"Claude Code's status line is back to what it was: {bridge.SettingsPath}");
            }
            return 0;
        }
        catch (ClaudeCodeSetupException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private async Task<int> DumpAsync()
    {
        var available = UsageStore.AllProviders().Where(p => p.IsAvailable()).ToList();
        // The status pages are read in parallel, while the usage is fetched.
        var statusClient = new ServiceStatusClient();
        var statuses = available.ToDictionary(p => p.Id, p => ReadStatusAsync(statusClient, p.Id));
        foreach (var provider in available)
        {
            Console.WriteLine($"== {provider.Id.DisplayName()}");
            if (await statuses[provider.Id] is { IsNotice: true } status) Console.WriteLine($"Status: {status.Summary(provider.Id)}");
            try
            {
                var result = await provider.FetchAsync();
                if (result.Snapshot.PlanName is { } plan) Console.WriteLine($"Plan: {plan}");
                foreach (var item in result.Snapshot.Items)
                {
                    var reset = item.ResetsAt is { } at ? " — " + UsageFormat.Reset(at) : "";
                    Console.WriteLine($"• {item.Title}: {UsageFormat.Percent(item.Percent)} [{item.Severity}]{reset}");
                }
                foreach (var note in result.Snapshot.Notes) Console.WriteLine($"i {note.Text}");
                if (args.Contains("--raw")) Console.WriteLine(System.Text.Encoding.UTF8.GetString(result.Cache));
            }
            catch (UsageException error)
            {
                Console.WriteLine($"! {error.MessageFor(provider.Id)}");
            }
        }
        if (ClaudeCode().LoadReading()?.Current(DateTimeOffset.Now) is { IsEmpty: false } claudeCode)
        {
            var seen = new[] { claudeCode.Session?.SeenAt, claudeCode.Weekly?.SeenAt }.Max();
            Console.WriteLine($"i Claude Code (statusline): {claudeCode.Summary()}" + (seen is { } at ? " — " + UsageFormat.Updated(at) : ""));
        }
        return 0;
    }

    /// <summary>The status page's answer for <c>--dump</c>; null when there is none or it could not be read.</summary>
    private static async Task<ServiceStatus?> ReadStatusAsync(IServiceStatusFetcher client, ProviderId id)
    {
        if (id.StatusPage() is not { } page) return null;
        try { return await client.FetchAsync(page); }
        catch (Exception) { return null; }
    }

    /// <summary>Every <c>--service-status claude=degraded</c>: the made-up statuses, by agent.</summary>
    internal static Dictionary<ProviderId, ServiceStatus> PreviewStatuses(string[] args)
    {
        var statuses = new Dictionary<ProviderId, ServiceStatus>();
        for (var index = Array.IndexOf(args, "--service-status"); index >= 0 && index + 1 < args.Length;
             index = Array.IndexOf(args, "--service-status", index + 1))
        {
            if (args[index + 1].Split('=', 2) is [var agent, var level]
                && PopupTabs.ProviderOf(agent) is { } id && ServiceStatus.Preview(id, level) is { } status)
            {
                statuses[id] = status;
            }
        }
        return statuses;
    }

    /// <summary>Applies the <c>--service-status</c> options to a fixture store.</summary>
    internal static void PreviewServiceStatuses(UsageStore store, string[] args)
    {
        foreach (var (id, status) in PreviewStatuses(args)) store.PreviewServiceStatus(id, status);
    }

    /// <summary>The snapshots of the <c>--*fixture</c> options, in display order.</summary>
    internal static List<ProviderSnapshot> FixtureSnapshots(string[] args)
    {
        var snapshots = new List<ProviderSnapshot>();
        if (Option(args, "--fixture") is { } claude &&
            ClaudeProvider.SnapshotFromResponse(File.ReadAllBytes(claude), "Max 5x") is { } claudeSnapshot)
        {
            snapshots.Add(claudeSnapshot);
        }
        if (Option(args, "--codex-fixture") is { } codex && CodexUsage.Snapshot(File.ReadAllBytes(codex)) is { } codexSnapshot)
        {
            snapshots.Add(codexSnapshot);
        }
        if (Option(args, "--copilot-fixture") is { } copilot && CopilotUsage.Snapshot(File.ReadAllBytes(copilot)) is { } copilotSnapshot)
        {
            snapshots.Add(copilotSnapshot);
        }
        if (Option(args, "--cursor-fixture") is { } cursor && CursorUsage.Snapshot(File.ReadAllBytes(cursor), "Pro") is { } cursorSnapshot)
        {
            snapshots.Add(cursorSnapshot);
        }
        if (Option(args, "--gemini-fixture") is { } gemini && GeminiUsage.Snapshot(File.ReadAllBytes(gemini), "Free") is { } geminiSnapshot)
        {
            snapshots.Add(geminiSnapshot);
        }
        if (Option(args, "--grok-fixture") is { } grok && GrokUsage.Snapshot(File.ReadAllBytes(grok)) is { } grokSnapshot)
        {
            snapshots.Add(grokSnapshot);
        }
        return snapshots;
    }

    /// <summary>Settings for fixture runs: never saved, and set from the command line.</summary>
    internal static AppSettings FixtureSettings(string[] args)
    {
        var settings = new AppSettings
        {
            IsTransient = true,
            LanguageValue = Localization.Current,
            OnboardingDismissed = !args.Contains("--first-run"),
        };
        if (Enum.TryParse<PopupLayout>(Option(args, "--layout"), ignoreCase: true, out var layout)) settings.Layout = layout;
        if (Option(args, "--tab-limit")?.Split('=', 2) is [var agent, var limit] && PopupTabs.ProviderOf(agent) is { } limited)
        {
            settings.TabLimits[limited.Key()] = limit;
        }
        return settings;
    }

    private int Snapshot()
    {
        var output = Path.GetFullPath(Option("--snapshot") ?? throw new ArgumentException("--snapshot needs a file name"));
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        Theme.Apply(forceDark: args.Contains("--dark"));

        var snapshots = FixtureSnapshots(args);
        var store = new UsageStore(FixtureSettings(args), snapshots);
        PreviewServiceStatuses(store, args);
        if (Option("--tab") is { } tab) store.PreviewTab(tab);
        var updater = AppVersion.Parse(Option("--update-available")) is { } update
            ? Updater.Preview(update, Updater.RunningVersion)
            : new Updater(current: null);
        var window = new PopupWindow(store, updater) { Notice = args.Contains("--no-tray-notice") ? App.NoTrayNotice : null };
        window.RenderToPng(output);
        window.Close();
        Console.WriteLine($"Saved {output}");

        var glyphs = new Dictionary<ProviderId, Glyph>();
        foreach (var snapshot in snapshots)
        {
            if (InstalledApps.Glyph(snapshot.Provider) is { } glyph) glyphs[snapshot.Provider] = glyph;
        }
        var trayPath = Path.ChangeExtension(output, null) + "-tray.png";
        File.WriteAllBytes(trayPath, TrayPreview.RenderPng(snapshots, glyphs, statuses: PreviewStatuses(args)));
        Console.WriteLine($"Saved {trayPath}");
        return 0;
    }

    private async Task<int> InstallUpdateAsync()
    {
        var updater = new Updater(AppVersion.Parse(Option("--as-version")) ?? Updater.RunningVersion)
        {
            ApiToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
        };
        await updater.CheckAsync(manual: true);
        if (updater.Available is not { } release)
        {
            if (updater.Phase == UpdatePhase.Failed)
            {
                Console.Error.WriteLine(updater.Failure);
                return 1;
            }
            Console.WriteLine($"Running {updater.CurrentText} ({updater.InstallMode}): nothing to install.");
            return 0;
        }
        Console.WriteLine($"Running {updater.CurrentText} ({updater.InstallMode}); installing {release.Version}.");
        if (!await updater.DownloadAndSwapAsync(relaunch: !args.Contains("--no-relaunch")))
        {
            Console.Error.WriteLine(updater.Failure);
            return 1;
        }
        Console.WriteLine($"Installed {release.Version}.");
        return 0;
    }
}
