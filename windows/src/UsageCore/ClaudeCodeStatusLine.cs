using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

/// <summary>
/// The shell Claude Code runs a status line command in: <c>/bin/sh</c> on Linux and macOS; on Windows,
/// Git Bash when it is installed and PowerShell otherwise (as Claude Code's documentation says).
/// </summary>
public sealed record ClaudeCodeShell(ClaudeCodeShell.Kind Type, string Program, bool WindowsPaths)
{
    public enum Kind { Posix, PowerShell }

    public static ClaudeCodeShell Detect() => OperatingSystem.IsWindows()
        ? ForWindows(Environment.GetEnvironmentVariable, File.Exists)
        : new ClaudeCodeShell(Kind.Posix, "/bin/sh", WindowsPaths: false);

    public static ClaudeCodeShell ForWindows(Func<string, string?> environment, Func<string, bool> exists) =>
        FindGitBash(environment, exists) is { } bash
            ? new ClaudeCodeShell(Kind.Posix, bash, WindowsPaths: true)
            : new ClaudeCodeShell(Kind.PowerShell, "powershell.exe", WindowsPaths: true);

    /// <summary>Where Claude Code looks: <c>CLAUDE_CODE_GIT_BASH_PATH</c>, next to <c>git.exe</c> on the PATH, then the usual install folders.</summary>
    internal static string? FindGitBash(Func<string, string?> environment, Func<string, bool> exists)
    {
        if (environment("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } configured && exists(configured)) return configured;
        var candidates = new List<string>();
        foreach (var folder in (environment("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = folder.TrimEnd('\\', '/');
            if (!exists(dir + @"\git.exe")) continue;
            // Git\cmd\git.exe, Git\bin\git.exe or Git\mingw64\bin\git.exe.
            candidates.Add(dir + @"\..\bin\bash.exe");
            candidates.Add(dir + @"\bash.exe");
            candidates.Add(dir + @"\..\..\bin\bash.exe");
        }
        foreach (var root in new[] { environment("ProgramFiles"), environment("ProgramFiles(x86)") })
        {
            if (root is { Length: > 0 }) candidates.Add(root.TrimEnd('\\') + @"\Git\bin\bash.exe");
        }
        if (environment("LOCALAPPDATA") is { Length: > 0 } local) candidates.Add(local.TrimEnd('\\') + @"\Programs\Git\bin\bash.exe");
        return candidates.FirstOrDefault(exists);
    }

    /// <summary>The command Claude Code runs: the app's executable, quoted for this shell.</summary>
    public string Command(string executable)
    {
        if (Type == Kind.PowerShell) return $"& '{executable.Replace("'", "''")}' {ClaudeCodeBridge.Flag}";
        // Git Bash reads backslashes as escapes: forward slashes, as Claude Code's documentation says.
        var path = WindowsPaths ? executable.Replace('\\', '/') : executable;
        return $"'{path.Replace("'", @"'\''")}' {ClaudeCodeBridge.Flag}";
    }

    /// <summary>Runs <paramref name="command"/> through this shell.</summary>
    public ProcessStartInfo StartInfo(string command)
    {
        var info = new ProcessStartInfo(Program) { UseShellExecute = false };
        if (Type == Kind.PowerShell)
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
        }
        else
        {
            info.ArgumentList.Add("-c");
        }
        info.ArgumentList.Add(command);
        return info;
    }
}

/// <summary>Claude Code's settings could not be read or written; they were left as they were.</summary>
public sealed class ClaudeCodeSetupException(string message) : Exception(message);

/// <summary>
/// Hooks the app into Claude Code's status line. Claude Code's <c>settings.json</c> gets
/// <c>"statusLine": {"type": "command", "command": "'…/AIUsageBar' --statusline"}</c>; that command keeps the
/// numbers (see <see cref="ClaudeCodeReading"/>) and then runs the user's own status line, if there was one,
/// so what Claude Code shows does not change. Turning it off puts the user's status line back.
/// Same rules as the Mac app.
/// </summary>
public sealed class ClaudeCodeBridge(string directory, string settingsPath, ClaudeCodeShell shell)
{
    public const string Flag = "--statusline";

    /// <summary>The app's own folder: the numbers and the user's previous status line.</summary>
    public string Directory { get; } = directory;

    /// <summary>Claude Code's user settings.</summary>
    public string SettingsPath { get; } = settingsPath;

    public ClaudeCodeShell Shell { get; } = shell;

    /// <summary>The switch in ⚙ → Claude.</summary>
    public static string MenuTitle => T("Ler uso pelo Claude Code (statusline)", "Read usage from Claude Code (status line)");

    /// <summary>Added to the "too many requests" notice while Claude has nothing to show and the switch is off.</summary>
    public static string Hint => T(
        "Usa o Claude Code no terminal? Ative ⚙ → Claude → Ler uso pelo Claude Code para ver os números sem esperar.",
        "Using Claude Code in a terminal? Turn on ⚙ → Claude → Read usage from Claude Code to see the numbers without waiting.");

    /// <summary><c>~/.claude/settings.json</c>, or <c>$CLAUDE_CONFIG_DIR/settings.json</c>.</summary>
    public static string DefaultSettingsPath()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var folder = string.IsNullOrEmpty(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
        return Path.Combine(folder, "settings.json");
    }

    private string ReadingPath => Path.Combine(Directory, "claude-code-usage.json");
    private string PreviousPath => Path.Combine(Directory, "claude-code-statusline.json");

    // MARK: The status line command

    /// <summary>
    /// What <c>--statusline</c> does: keeps the numbers, then hands the same input to the user's own status
    /// line and returns its exit code (no output and 0 when there is none).
    /// </summary>
    public int RunStatusLine(byte[] input, Stream output, DateTimeOffset now)
    {
        Record(input, now);
        return PreviousCommand() is { } previous ? Run(previous, input, output) : 0;
    }

    /// <summary>Merges the numbers in one status line input into the saved ones.</summary>
    public void Record(byte[] input, DateTimeOffset now)
    {
        if (ClaudeCodeReading.FromStatusLine(input, now, ModificationDate) is not { IsEmpty: false } reading) return;
        var old = LoadReading();
        var merged = (old ?? new ClaudeCodeReading()).Merged(reading, now);
        if (merged == old) return;
        try
        {
            WriteAtomically(ReadingPath, merged.Encode());
        }
        catch (Exception)
        {
            // The next response tries again.
        }
    }

    public ClaudeCodeReading? LoadReading()
    {
        try
        {
            return File.Exists(ReadingPath) ? ClaudeCodeReading.Decode(File.ReadAllBytes(ReadingPath)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>When the saved numbers last changed, so the app reloads them only then.</summary>
    public DateTimeOffset? ReadingDate() => ModificationDate(ReadingPath);

    /// <summary>The user's own status line command, saved when the app's was put in.</summary>
    public string? PreviousCommand()
    {
        if (Previous() is not { Object: JsonObject line }) return null;
        if (Json.Str(line, "type") != "command" || Json.Str(line, "command") is not { } command) return null;
        return string.IsNullOrWhiteSpace(command) || IsOurs(command) ? null : command;
    }

    /// <summary>The user's <c>statusLine</c> from before, as an object and as the exact text it had in the file.</summary>
    private (JsonNode? Object, string? Text)? Previous()
    {
        try
        {
            if (!File.Exists(PreviousPath) || JsonNode.Parse(File.ReadAllText(PreviousPath)) is not JsonObject root) return null;
            return (root["previous"], Json.Str(root, "previous_json"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SavePrevious(JsonNode? value, string? text)
    {
        var root = new JsonObject { ["previous"] = value?.DeepClone(), ["previous_json"] = text };
        WriteAtomically(PreviousPath, Encoding.UTF8.GetBytes(root.ToJsonString(Indented)));
    }

    /// <summary>Runs the user's status line the way Claude Code does (through its shell), with the same input.</summary>
    private int Run(string command, byte[] input, Stream output)
    {
        var info = Shell.StartInfo(command);
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        try
        {
            using var process = Process.Start(info);
            if (process is null) return 127;
            try
            {
                process.StandardInput.BaseStream.Write(input);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The user's command exited without reading its input.
            }
            process.StandardOutput.BaseStream.CopyTo(output);
            output.Flush();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception)
        {
            return 127;
        }
    }

    private static DateTimeOffset? ModificationDate(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Written to a temporary file and moved: sessions writing at once never leave half a file.</summary>
    private static void WriteAtomically(string path, byte[] data)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, path, overwrite: true);
    }

    // MARK: Claude Code's settings

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>The app's command, whatever path or shell it was put in with.</summary>
    public static bool IsOurs(string command) => command.Contains("AIUsageBar", StringComparison.Ordinal) && command.Contains(Flag, StringComparison.Ordinal);

    /// <summary>Whether Claude Code's status line is the app's.</summary>
    public bool IsInstalled()
    {
        try
        {
            return OurCommand(ReadSettings().Settings) is not null;
        }
        catch (ClaudeCodeSetupException)
        {
            return false;
        }
    }

    private static string? OurCommand(JsonObject settings) =>
        Json.Str(Json.Obj(settings, "statusLine"), "command") is { } command && IsOurs(command) ? command : null;

    /// <summary>Puts the app's command in. The user's status line is kept, to run after it and to put back.</summary>
    public void Install(string executable)
    {
        var (text, settings) = ReadSettings();
        var current = settings["statusLine"];
        if (OurCommand(settings) is null)
        {
            // Saved first: if writing the settings fails, nothing is lost.
            try
            {
                SavePrevious(current, text is null ? null : JsonText.ValueOf("statusLine", text));
            }
            catch (Exception error)
            {
                throw WriteFailure(error);
            }
        }
        // The user's padding and refresh interval stay: their status line still runs.
        var line = current is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
        line["type"] = "command";
        line["command"] = Shell.Command(executable);
        WriteSettings(text, line.ToJsonString(Compact));
    }

    /// <summary>Points the command at where the app is now (and at the shell Claude Code now uses), when it is in.</summary>
    public void Refresh(string executable)
    {
        try
        {
            var (text, settings) = ReadSettings();
            if (OurCommand(settings) is not { } current || settings["statusLine"] is not JsonObject existing) return;
            var command = Shell.Command(executable);
            if (current == command) return;
            var line = (JsonObject)existing.DeepClone();
            line["command"] = command;
            WriteSettings(text, line.ToJsonString(Compact));
        }
        catch (ClaudeCodeSetupException)
        {
            // Tried again at the next launch.
        }
    }

    /// <summary>Puts the user's status line back (or none), only when the current one is the app's, and forgets the numbers.</summary>
    public void Uninstall()
    {
        var (text, settings) = ReadSettings();
        if (OurCommand(settings) is not null)
        {
            var saved = Previous();
            WriteSettings(text, saved?.Text ?? saved?.Object?.ToJsonString(Compact));
        }
        foreach (var path in new[] { PreviousPath, ReadingPath })
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }

    /// <summary>The file's text and its object; none and an empty object when there is no file yet.</summary>
    private (string? Text, JsonObject Settings) ReadSettings()
    {
        string text;
        try
        {
            if (!File.Exists(SettingsPath)) return (null, new JsonObject());
            text = File.ReadAllText(SettingsPath);
        }
        catch (Exception)
        {
            throw Unreadable();
        }
        if (string.IsNullOrWhiteSpace(text)) return (null, new JsonObject());
        try
        {
            return JsonNode.Parse(text) is JsonObject settings ? (text, settings) : throw Unreadable();
        }
        catch (JsonException)
        {
            throw Unreadable();
        }
    }

    /// <summary>Changes only the <c>statusLine</c> member, so the rest of the file stays exactly as it was.</summary>
    private void WriteSettings(string? text, string? statusLine)
    {
        try
        {
            WriteAtomically(SettingsPath, new UTF8Encoding(false).GetBytes(JsonText.Setting("statusLine", statusLine, text)));
        }
        catch (Exception error)
        {
            throw WriteFailure(error);
        }
    }

    private static ClaudeCodeSetupException Unreadable() => new(T(
        "Não consegui ler o settings.json do Claude Code, então não mexi nele. Confira se é um JSON válido.",
        "Couldn't read Claude Code's settings.json, so it was left alone. Check that it is valid JSON."));

    private static ClaudeCodeSetupException WriteFailure(Exception error) => new(T(
        $"Não consegui gravar o settings.json do Claude Code: {error.Message}",
        $"Couldn't write Claude Code's settings.json: {error.Message}"));
}

/// <summary>
/// Edits one top-level member of a JSON object's text and leaves every other character alone.
/// The text must already be valid JSON (checked by the caller).
/// </summary>
internal static class JsonText
{
    internal sealed record Member(string Key, int Start, int ValueStart, int ValueEnd);

    /// <summary>The member's value as it is written in the file.</summary>
    public static string? ValueOf(string key, string text) =>
        Members(text) is { } found && found.Members.FirstOrDefault(m => m.Key == key) is { } member
            ? text[member.ValueStart..member.ValueEnd]
            : null;

    /// <summary>
    /// <paramref name="text"/> with the member <paramref name="key"/> set to <paramref name="value"/> (JSON text):
    /// replaced where it is, added at the end when missing, removed when <paramref name="value"/> is null.
    /// </summary>
    public static string Setting(string key, string? value, string? text)
    {
        var quotedKey = JsonSerializer.Serialize(key);
        if (text is null || Members(text) is not { } found)
        {
            return value is null ? "{}\n" : $"{{\n  {quotedKey}: {value}\n}}\n";
        }
        var (open, close, list) = found;
        var index = list.FindIndex(m => m.Key == key);
        if (index >= 0)
        {
            var member = list[index];
            if (value is not null) return text[..member.ValueStart] + value + text[member.ValueEnd..];
            // From the end of the previous member: its comma goes too.
            if (index > 0) return text[..list[index - 1].ValueEnd] + text[member.ValueEnd..];
            if (list.Count > 1) return text[..member.Start] + text[list[1].Start..];
            return text[..(open + 1)] + "\n" + text[close..];
        }
        if (value is null) return text;
        var indent = Indentation(list.FirstOrDefault(), text) ?? "  ";
        if (list.Count > 0)
        {
            var end = list[^1].ValueEnd;
            return text[..end] + $",\n{indent}{quotedKey}: {value}" + text[end..];
        }
        return text[..(open + 1)] + $"\n{indent}{quotedKey}: {value}\n" + text[close..];
    }

    /// <summary>The spaces before the first key on its line, to add a member the same way.</summary>
    private static string? Indentation(Member? member, string text)
    {
        if (member is null) return null;
        var start = member.Start;
        while (start > 0 && text[start - 1] is ' ' or '\t') start--;
        return start > 0 && text[start - 1] == '\n' && start < member.Start ? text[start..member.Start] : null;
    }

    internal static (int Open, int Close, List<Member> Members)? Members(string t)
    {
        var i = t.Length > 0 && t[0] == '﻿' ? 1 : 0;
        void SkipSpace()
        {
            while (i < t.Length && t[i] is ' ' or '\n' or '\r' or '\t') i++;
        }
        SkipSpace();
        if (i >= t.Length || t[i] != '{') return null;
        var open = i++;
        var list = new List<Member>();
        while (true)
        {
            SkipSpace();
            if (i >= t.Length) return null;
            if (t[i] == '}') return (open, i, list);
            if (t[i] == ',') { i++; continue; }
            if (t[i] != '"' || EndOfString(t, i) is not { } keyEnd) return null;
            var start = i;
            string key;
            try { key = JsonSerializer.Deserialize<string>(t[start..keyEnd]) ?? ""; }
            catch (JsonException) { key = ""; }
            i = keyEnd;
            SkipSpace();
            if (i >= t.Length || t[i] != ':') return null;
            i++;
            SkipSpace();
            if (EndOfValue(t, i) is not { } valueEnd) return null;
            list.Add(new Member(key, start, i, valueEnd));
            i = valueEnd;
        }
    }

    private static int? EndOfString(string t, int start)
    {
        for (var j = start + 1; j < t.Length; j++)
        {
            if (t[j] == '\\') { j++; continue; }
            if (t[j] == '"') return j + 1;
        }
        return null;
    }

    private static int? EndOfValue(string t, int start)
    {
        if (start >= t.Length) return null;
        switch (t[start])
        {
            case '"':
                return EndOfString(t, start);
            case '{' or '[':
                var depth = 0;
                for (var j = start; j < t.Length; j++)
                {
                    switch (t[j])
                    {
                        case '"':
                            if (EndOfString(t, j) is not { } end) return null;
                            j = end - 1;
                            break;
                        case '{' or '[':
                            depth++;
                            break;
                        case '}' or ']':
                            if (--depth == 0) return j + 1;
                            break;
                    }
                }
                return null;
            default:
                var k = start;
                while (k < t.Length && t[k] is not (' ' or '\n' or '\r' or '\t' or ',' or '}' or ']')) k++;
                return k;
        }
    }
}
