using AIUsageBar.Core;

namespace UsageCore.Tests;

/// <summary>Where the Claude Code login is looked for on Windows and Linux.</summary>
public class ClaudeCredentialsTests
{
    private const string Home = "/home/someone";

    private static string Directory(params (string Key, string? Value)[] environment)
    {
        var map = environment.ToDictionary(entry => entry.Key, entry => entry.Value);
        return ClaudeCredentialsStore.ConfigDirectory(
            key => map.TryGetValue(key, out var value) ? value : null,
            Home);
    }

    [Fact]
    public void DefaultsToTheHomeDirectory()
    {
        Assert.Equal(Path.Combine(Home, ".claude"), Directory());
    }

    [Fact]
    public void ConfigDirectoryIsUsedWhenSet()
    {
        Assert.Equal("/opt/claude", Directory(("CLAUDE_CONFIG_DIR", "/opt/claude")));
    }

    [Fact]
    public void SecureStorageDirectoryWins()
    {
        Assert.Equal("/opt/secure", Directory(
            ("CLAUDE_CONFIG_DIR", "/opt/claude"),
            ("CLAUDE_SECURESTORAGE_CONFIG_DIR", "/opt/secure")));
    }

    [Fact]
    public void AnEmptyValueMeansTheDefault()
    {
        var expected = Path.Combine(Home, ".claude");
        Assert.Equal(expected, Directory(("CLAUDE_CONFIG_DIR", "")));
        Assert.Equal(expected, Directory(
            ("CLAUDE_CONFIG_DIR", "/opt/claude"),
            ("CLAUDE_SECURESTORAGE_CONFIG_DIR", "")));
    }

    [Fact]
    public void AnExpiredLoginIsReportedWithoutLeakingTheToken()
    {
        var expired = new ClaudeCredentials(
            "sk-ant-oat-secret", DateTimeOffset.UtcNow.AddDays(-180), "max", "default_claude_max_5x");
        Assert.True(expired.IsExpired);
        Assert.Equal("Max 5x", expired.PlanName);
        Assert.DoesNotContain("sk-ant-oat-secret", expired.ToString());
    }
}
