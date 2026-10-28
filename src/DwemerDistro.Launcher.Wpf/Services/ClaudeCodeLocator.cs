using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

public enum ClaudeCodeState
{
    NotFound,
    TooOld,
    SignedOut,
    /// <summary>Found, but it did not answer a version, help or sign-in check in time or in a readable form.</summary>
    Unavailable,
    Ready
}

/// <summary>Readiness of the locally installed Claude Code CLI. Never carries account identity or credentials.</summary>
public sealed record ClaudeCodeStatus(ClaudeCodeState State, string ExecutablePath = "", Version? Version = null, string AuthMethod = "")
{
    public string VersionText => Version?.ToString(3) ?? string.Empty;
}

/// <summary>
/// Finds the user's own signed-in <c>claude.exe</c> and checks it can run a DwemerDistro turn. It only runs
/// <c>--version</c>, <c>--help</c> and <c>auth status</c>; it never opens Claude's settings or credential files, and of
/// the sign-in status it keeps only <c>loggedIn</c> and <c>authMethod</c>.
/// </summary>
public sealed class ClaudeCodeLocator
{
    /// <summary>The first release with <c>--permission-prompts none</c>; every other required flag is checked in its help.</summary>
    internal static readonly Version MinimumVersion = new(2, 1, 259);

    /// <summary>Every flag <see cref="ClaudeTurnProcess.BuildArguments"/> passes. A CLI whose help lacks one is too old.</summary>
    internal static readonly string[] RequiredFlags =
    [
        "--print", "--input-format", "--output-format", "--verbose", "--include-partial-messages", "--tools", "--restricted",
        "--strict-mcp-config", "--mcp-config", "--settings", "--allowedTools", "--permission-mode", "--permission-prompts",
        "--disable-slash-commands", "--system-prompt", "--model", "--session-id", "--resume"
    ];

    /// <summary>
    /// Sign-ins that use the user's Claude subscription. Anything else (Console or API key, third-party providers) could
    /// bill an API account, so it reads as signed out.
    /// </summary>
    internal static readonly string[] AcceptedAuthMethods = ["claude.ai"];

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private const int MaxOutputChars = 64 * 1024;
    private static readonly Regex VersionPattern = new(@"^\s*(\d{1,4})\.(\d{1,4})\.(\d{1,6})\b");

    private readonly ProcessRunner _runner;
    private readonly string? _localAppData;

    public ClaudeCodeLocator(ProcessRunner runner, string? localAppDataDirectory = null)
    {
        _runner = runner;
        _localAppData = localAppDataDirectory;
    }

    public async Task<ClaudeCodeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var path = Locate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("PATH"));
        if (path is null) return new ClaudeCodeStatus(ClaudeCodeState.NotFound);

        string workDirectory;
        try
        {
            workDirectory = ClaudeTurnProcess.EnsureWorkDirectory(_localAppData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLogService.Operation("Claude Code check could not prepare its working folder.");
            return new ClaudeCodeStatus(ClaudeCodeState.Unavailable, path);
        }

        var versionOutput = await RunBoundedAsync(path, ["--version"], workDirectory, cancellationToken).ConfigureAwait(false);
        if (versionOutput is null) return new ClaudeCodeStatus(ClaudeCodeState.Unavailable, path);
        var version = ParseVersion(versionOutput);
        if (version is null) return new ClaudeCodeStatus(ClaudeCodeState.Unavailable, path);
        if (version < MinimumVersion) return new ClaudeCodeStatus(ClaudeCodeState.TooOld, path, version);

        var help = await RunBoundedAsync(path, ["--help"], workDirectory, cancellationToken).ConfigureAwait(false);
        if (help is null) return new ClaudeCodeStatus(ClaudeCodeState.Unavailable, path, version);
        if (!SupportsRequiredFlags(help)) return new ClaudeCodeStatus(ClaudeCodeState.TooOld, path, version);

        // auth status exits non-zero when signed out but still prints its JSON, so the exit code is not checked.
        var auth = await RunBoundedAsync(path, ["auth", "status", "--json"], workDirectory, cancellationToken, requireSuccess: false)
            .ConfigureAwait(false);
        if (auth is null) return new ClaudeCodeStatus(ClaudeCodeState.Unavailable, path, version);
        var method = ReadAuthMethod(auth, out var readable);
        if (!readable) return new ClaudeCodeStatus(ClaudeCodeState.Unavailable, path, version);
        if (method is null) return new ClaudeCodeStatus(ClaudeCodeState.SignedOut, path, version);
        return new ClaudeCodeStatus(ClaudeCodeState.Ready, path, version, method);
    }

    /// <summary>
    /// <c>%USERPROFILE%\.local\bin\claude.exe</c> (the native installer), then the first <c>claude.exe</c> in an absolute
    /// PATH entry. A candidate under a junction or symlink is skipped. Script shims (claude.cmd) are never used, because
    /// running one needs a shell.
    /// </summary>
    internal static string? Locate(string? userProfile, string? pathVariable)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(userProfile) && Path.IsPathFullyQualified(userProfile))
            candidates.Add(Path.Combine(userProfile, ".local", "bin", "claude.exe"));
        foreach (var raw in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim().Trim('"');
            // A relative entry (".", "bin") would resolve against whatever folder the launcher runs in.
            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry)) continue;
            candidates.Add(Path.Combine(entry, "claude.exe"));
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full) && !HasReparsePoint(full)) return full;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
        return null;
    }

    /// <summary>True when the path or any folder above it is a junction, symlink or other reparse point, or cannot be checked.</summary>
    internal static bool HasReparsePoint(string fullPath)
    {
        try
        {
            FileSystemInfo? entry = File.Exists(fullPath) ? new FileInfo(fullPath) : new DirectoryInfo(fullPath);
            while (entry is not null)
            {
                if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    internal static Version? ParseVersion(string output)
    {
        var match = VersionPattern.Match(output);
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value))
            : null;
    }

    internal static bool SupportsRequiredFlags(string help) =>
        RequiredFlags.All(flag => Regex.IsMatch(help, @"(^|[\s,])" + Regex.Escape(flag) + @"([\s,=]|$)", RegexOptions.Multiline));

    /// <summary>
    /// The accepted sign-in method from <c>auth status</c> JSON, or null when signed out or signed in some other way.
    /// <paramref name="readable"/> is false when the output is not that JSON. No other field is read or kept.
    /// </summary>
    internal static string? ReadAuthMethod(string output, out bool readable)
    {
        readable = false;
        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            readable = true;
            if (loggedIn.ValueKind != JsonValueKind.True) return null;
            var method = AssistantCliService.GetString(root, "authMethod");
            return AcceptedAuthMethods.Contains(method, StringComparer.Ordinal) ? method : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs one short CLI check in a kill-on-close job with the sanitized environment, and returns its stdout (capped), or
    /// null on timeout, failure or oversize. Stderr is drained and dropped; only a generic line is ever logged.
    /// </summary>
    private async Task<string?> RunBoundedAsync(string path, string[] arguments, string workDirectory, CancellationToken cancellationToken,
        bool requireSuccess = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        using var job = WindowsJobObject.TryCreate();
        using var process = new Process { StartInfo = ClaudeTurnProcess.CreateStartInfo(path, arguments, workDirectory, redirectInput: false) };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LauncherLogService.Operation("Claude Code check could not start.");
            return null;
        }
        job?.TryAssign(process);

        try
        {
            var stdout = ReadCappedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadCappedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (text is null || requireSuccess && process.ExitCode != 0)
            {
                LauncherLogService.Operation("Claude Code check failed.");
                return null;
            }
            return text;
        }
        catch (OperationCanceledException)
        {
            job?.Terminate();
            _runner.TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            LauncherLogService.Operation("Claude Code check timed out.");
            return null;
        }
    }

    private static async Task<string?> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        var oversize = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (text.Length + read > MaxOutputChars) oversize = true;
            else text.Append(buffer, 0, read);
        }
        return oversize ? null : text.ToString();
    }
}
