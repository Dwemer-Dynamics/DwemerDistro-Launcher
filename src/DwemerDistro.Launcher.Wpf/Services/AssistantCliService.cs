using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>One <c>ddistro --json</c> document, or a launcher-side failure when none was readable.</summary>
public sealed record AssistantCliResult(
    bool Ok,
    string Command,
    string? OperationId,
    JsonElement Data,
    string ErrorCode,
    string ErrorMessage,
    IReadOnlyList<string> Warnings,
    bool HasDocument)
{
    // Launcher-side codes. Core codes (busy, conflict, partial...) arrive verbatim from the document.
    public const string CliMissing = "cli_missing";
    public const string DistroMissing = "distro_missing";
    public const string Protocol = "protocol";
    public const string Timeout = "timeout";

    /// <summary><c>error.details</c>: for a failed apply or rollback, Core's per-file result lives here, not in Data.</summary>
    public JsonElement ErrorDetails { get; init; }

    /// <summary>How many warnings the document carried; <see cref="Warnings"/> may hold fewer.</summary>
    public int WarningTotal { get; init; }

    internal static AssistantCliResult Failure(string code, string message) =>
        new(false, string.Empty, null, default, code, message, [], HasDocument: false);
}

public interface IAssistantCli
{
    /// <summary>The WSL distro every command and session of this CLI reaches.</summary>
    string DistroName => LauncherConstants.DistroName;

    Task<AssistantCliResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Starts <c>ddistro agent serve</c>: a raw Codex App Server JSONL stream for one task.</summary>
    Task<IAssistantTransport> StartServeAsync(string taskId, CancellationToken cancellationToken);
}

public interface IAssistantTransport : IAsyncDisposable
{
    /// <summary>Returns the next line, or null once the stream has closed.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>Bounded stderr tail for a disconnect message. Never logged.</summary>
    string DiagnosticTail { get; }
}

/// <summary>
/// Allowlisted argument vectors for the Core assistant contract. Task ids, hashes, products and
/// relative paths are validated here, so nothing typed into the launcher reaches the CLI unchecked.
/// </summary>
public static partial class AssistantCliCommands
{
    public const string CliPath = "/usr/local/bin/ddistro";
    public const int MaxRelativePaths = 20;

    public static string[] Capabilities() => ["capabilities"];
    public static string[] AgentStatus() => ["agent", "status"];
    public static string[] AgentProbe() => ["agent", "probe"];
    public static string[] Diff(string taskId) => ["changes", "diff", RequireTaskId(taskId)];
    public static string[] Stage(string taskId) => ["changes", "stage", RequireTaskId(taskId)];
    public static string[] ChangeStatus(string taskId) => ["changes", "status", RequireTaskId(taskId)];
    public static string[] Rollback(string taskId) => ["changes", "rollback", RequireTaskId(taskId)];

    public static string[] Apply(string taskId, string manifestSha256)
    {
        if (!Sha256Regex().IsMatch(manifestSha256 ?? string.Empty))
            throw new ArgumentException("The manifest hash must be 64 lowercase hex characters.", nameof(manifestSha256));
        return ["changes", "apply", RequireTaskId(taskId), "--manifest-sha256", manifestSha256!];
    }

    public static string[] Prepare(string product, IReadOnlyList<string> relativePaths)
    {
        if (product != "herika")
            throw new ArgumentException("Only CHIM (herika) is supported.", nameof(product));
        if (relativePaths.Count > MaxRelativePaths)
            throw new ArgumentException($"Select at most {MaxRelativePaths} files.", nameof(relativePaths));
        var arguments = new List<string> { "changes", "prepare", "--product", product };
        foreach (var path in relativePaths)
        {
            if (!IsSafeRelativePath(path))
                throw new ArgumentException("Paths must be relative to the product folder.", nameof(relativePaths));
            arguments.Add("--path");
            arguments.Add(path);
        }
        return [.. arguments];
    }

    /// <summary>Plain relative paths only: no root, drive, parent step, option prefix or shell characters.</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path[0] == '/' || path[0] == '-' || !RelativePathRegex().IsMatch(path))
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    public static bool IsValidTaskId(string? taskId) => TaskIdRegex().IsMatch(taskId ?? string.Empty);

    public static bool IsValidManifestHash(string? hash) => Sha256Regex().IsMatch(hash ?? string.Empty);

    private static string RequireTaskId(string taskId) =>
        IsValidTaskId(taskId) ? taskId : throw new ArgumentException("Invalid assistant task id.", nameof(taskId));

    /// <summary>Read-only queries run as the application user; the root broker owns everything else.</summary>
    internal static string UserFor(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0] == "capabilities" ? LauncherConstants.DistroUser : "root";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}\z")]
    private static partial Regex TaskIdRegex();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+\z")]
    private static partial Regex RelativePathRegex();
}

public sealed partial class AssistantCliService : IAssistantCli
{
    private const int MaxDocumentChars = 8 * 1024 * 1024;
    private readonly WslService _wsl;
    private readonly ProcessRunner _processRunner;

    public AssistantCliService(WslService wsl, ProcessRunner processRunner)
        : this(wsl, processRunner, LauncherConstants.DistroName)
    {
    }

    /// <summary>
    /// Test seam for an isolated, separately registered distro. Only the distro name changes: the CLI path,
    /// users and supervision stay fixed, and every query, session and cleanup reaches this one distro.
    /// </summary>
    internal AssistantCliService(WslService wsl, ProcessRunner processRunner, string distroName)
    {
        if (!IsPlainDistroName(distroName))
            throw new ArgumentException("The distro name must be a plain WSL distro name.", nameof(distroName));
        _wsl = wsl;
        _processRunner = processRunner;
        DistroName = distroName;
    }

    public string DistroName { get; }

    internal static bool IsPlainDistroName(string? name) => DistroNameRegex().IsMatch(name ?? string.Empty);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex DistroNameRegex();

    public async Task<AssistantCliResult> RunAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        string[] cli = [AssistantCliCommands.CliPath, .. arguments, "--json", "--request-id", requestId];
        var user = AssistantCliCommands.UserFor(arguments);
        var verb = string.Join(" ", arguments.Take(2));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        LauncherLogService.Operation($"Assistant START {verb} ({requestId})");
        CommandResult result;
        try
        {
            result = user == "root"
                ? await RunSupervisedAsync(cli, timeoutCts.Token).ConfigureAwait(false)
                : await _wsl.RunWslAsync(ExecArguments(user, cli), cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LauncherLogService.Operation($"Assistant END {verb}; timed out");
            return AssistantCliResult.Failure(AssistantCliResult.Timeout, "DwemerDistro did not answer in time. The outcome is unknown.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            LauncherLogService.Operation($"Assistant END {verb}; wsl.exe unavailable");
            return AssistantCliResult.Failure(AssistantCliResult.DistroMissing, "WSL is not available on this PC.");
        }

        var parsed = Parse(result);
        LauncherLogService.Operation($"Assistant END {verb}; exit code {result.ExitCode}; ok={parsed.Ok}; code={parsed.ErrorCode}");
        return parsed;
    }

    public Task<IAssistantTransport> StartServeAsync(string taskId, CancellationToken cancellationToken)
    {
        var pidFile = NewPidFile();
        string[] cli = [AssistantCliCommands.CliPath, "agent", "serve", "--task",
            AssistantCliCommands.IsValidTaskId(taskId) ? taskId : throw new ArgumentException("Invalid assistant task id.", nameof(taskId))];
        cancellationToken.ThrowIfCancellationRequested();
        LauncherLogService.Operation("Assistant session START");
        var process = _processRunner.StartHiddenDuplexProcess("wsl.exe", ExecArguments("root", BuildSupervisor(pidFile, cli)));
        var transport = new SupervisedTransport(process, _processRunner, () => StopGroupAsync(pidFile, true));
        transport.StartStderrDrain();
        return Task.FromResult<IAssistantTransport>(transport);
    }

    /// <summary>
    /// Root commands run as their own Linux process group recorded in a private pid file, because
    /// ending wsl.exe does not reliably end processes inside the distro (see DistroUpdateRunner).
    /// </summary>
    private async Task<CommandResult> RunSupervisedAsync(string[] cli, CancellationToken cancellationToken)
    {
        var pidFile = NewPidFile();
        try
        {
            return await _wsl.RunWslAsync(ExecArguments("root", BuildSupervisor(pidFile, cli)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopGroupAsync(pidFile, cancellationToken.IsCancellationRequested).ConfigureAwait(false);
        }
    }

    private async Task StopGroupAsync(string pidFile, bool waitForPidFile)
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var script = (waitForPidFile ? "for attempt in 1 2 3 4 5; do [ -s \"$1\" ] && break; sleep 0.2; done; " : "") +
            "if [ -s \"$1\" ]; then read -r pid < \"$1\"; " +
            "case \"$pid\" in ''|*[!0-9]*) exit 1;; esac; " +
            "if kill -0 -- -\"$pid\" 2>/dev/null; then kill -TERM -- -\"$pid\" 2>/dev/null; sleep 1; " +
            "kill -KILL -- -\"$pid\" 2>/dev/null || true; fi; rm -f \"$1\"; fi";
        try
        {
            var result = await _wsl.RunWslAsync(ExecArguments("root", ["bash", "-c", script, "ddistro-assistant-stop", pidFile]),
                cancellationToken: cleanupTimeout.Token).ConfigureAwait(false);
            if (!result.Succeeded) LauncherLogService.Operation("Assistant could not confirm its Linux process group stopped.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            LauncherLogService.Operation("Assistant process cleanup did not complete.");
        }
    }

    private static string NewPidFile() => "/run/dwemerdistro-assistant-" + Guid.NewGuid().ToString("N") + ".pid";

    /// <summary>The one route to wsl.exe for queries, sessions and cleanup, so all of them reach <see cref="DistroName"/>.</summary>
    internal string[] ExecArguments(string user, IReadOnlyList<string> command) => BuildExecArguments(user, command, DistroName);

    // --exec bypasses the default shell, so every element reaches Linux as one argument.
    internal static string[] BuildExecArguments(string user, IReadOnlyList<string> command, string distroName = LauncherConstants.DistroName) =>
        ["-d", distroName, "-u", user, "--exec", .. command];

    internal static string[] BuildSupervisor(string pidFile, IReadOnlyList<string> cli) =>
    [
        "setsid", "--wait", "bash", "-c",
        "umask 077; printf '%s\\n' \"$$\" > \"$1\" || exit 1; shift; exec \"$@\"",
        "ddistro-assistant", pidFile, .. cli
    ];

    internal static AssistantCliResult Parse(CommandResult result)
    {
        var text = result.StandardOutput.Replace("\0", string.Empty).Trim();
        if (text.Length == 0 || text[0] != '{' || text.Length > MaxDocumentChars)
            return ClassifyMissingDocument(result);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
                !root.TryGetProperty("ok", out var okElement) || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return AssistantCliResult.Failure(AssistantCliResult.Protocol, "DwemerDistro returned an unsupported assistant response. Update DwemerDistro.");
            }

            var ok = okElement.GetBoolean();
            var code = string.Empty;
            var message = string.Empty;
            JsonElement details = default;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = GetString(error, "code");
                message = GetString(error, "message");
                if (error.TryGetProperty("details", out var errorDetails) && errorDetails.ValueKind == JsonValueKind.Object)
                    details = errorDetails.Clone();
            }
            if (!ok && code.Length == 0)
                return AssistantCliResult.Failure(AssistantCliResult.Protocol, "DwemerDistro reported a failure without an error code.");
            // A failed process never counts as a success, and a clean exit never hides a failure.
            if (ok != (result.ExitCode == 0))
                return AssistantCliResult.Failure(AssistantCliResult.Protocol,
                    $"DwemerDistro's exit code {result.ExitCode} contradicts its assistant response. The outcome is unknown.");

            var warnings = root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : [];
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            var operationId = GetString(root, "operation_id");
            return new AssistantCliResult(ok, GetString(root, "command"), operationId.Length > 0 ? operationId : null,
                data, code, message, warnings.Take(50).ToArray(), HasDocument: true)
            {
                ErrorDetails = details,
                WarningTotal = warnings.Length
            };
        }
        catch (JsonException)
        {
            return AssistantCliResult.Failure(AssistantCliResult.Protocol, "DwemerDistro returned an unreadable assistant response.");
        }
    }

    private static AssistantCliResult ClassifyMissingDocument(CommandResult result)
    {
        var combined = (result.StandardOutput + "\n" + result.StandardError).Replace("\0", string.Empty);
        if (combined.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("There is no distribution", StringComparison.OrdinalIgnoreCase))
            return AssistantCliResult.Failure(AssistantCliResult.DistroMissing, "The DwemerDistro WSL distro is not installed.");
        if (result.ExitCode is 126 or 127 ||
            combined.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("execvpe", StringComparison.OrdinalIgnoreCase))
            return AssistantCliResult.Failure(AssistantCliResult.CliMissing, "This DwemerDistro has no ddistro command. Run Update Distro.");
        return AssistantCliResult.Failure(AssistantCliResult.Protocol, $"DwemerDistro returned no assistant response (exit code {result.ExitCode}).");
    }

    internal static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    internal static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>File lists may be plain paths or objects with a path, action and outcome; both read as one line each.</summary>
    internal static string[] GetFiles(JsonElement element, string name, int limit = 200)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var files) || files.ValueKind != JsonValueKind.Array)
            return [];
        return files.EnumerateArray().Take(limit).Select(file => file.ValueKind switch
        {
            JsonValueKind.String => file.GetString() ?? string.Empty,
            JsonValueKind.Object => DescribeFile(file),
            _ => string.Empty
        }).Where(line => line.Length > 0).ToArray();
    }

    private static string DescribeFile(JsonElement file)
    {
        var path = GetString(file, "path");
        var action = GetString(file, "action");
        if (action.Length == 0) action = GetString(file, "status");
        // Apply and rollback receipts add each file's own outcome: written, failed, pending or unknown.
        var outcome = GetString(file, "state");
        var label = string.Join(", ", new[] { action, outcome }.Where(part => part.Length > 0));
        return label.Length > 0 ? $"{path} ({label})" : path;
    }

    /// <summary>Per-file outcome counts from a receipt or journal file list. Files without an outcome are not counted.</summary>
    internal static Dictionary<string, int> CountFileStates(JsonElement element)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return counts;
        foreach (var state in files.EnumerateArray().Select(file => GetString(file, "state")).Where(state => state.Length > 0))
            counts[state] = counts.GetValueOrDefault(state) + 1;
        return counts;
    }

    private sealed class SupervisedTransport(Process process, ProcessRunner runner, Func<Task> stopGroup) : IAssistantTransport
    {
        private const int MaxStderrTail = 2048;
        private readonly BoundedLineReader _stdout = new(process.StandardOutput);
        private readonly StringBuilder _stderr = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public string DiagnosticTail
        {
            get { lock (_stderr) return _stderr.ToString(); }
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => _stdout.ReadLineAsync(cancellationToken);

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteAsync((line + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        internal void StartStderrDrain() => _ = Task.Run(async () =>
        {
            var buffer = new char[1024];
            try
            {
                int read;
                while ((read = await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    lock (_stderr)
                    {
                        _stderr.Append(buffer, 0, read);
                        if (_stderr.Length > MaxStderrTail) _stderr.Remove(0, _stderr.Length - MaxStderrTail);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            using (var exitWait = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                try { await process.WaitForExitAsync(exitWait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            await stopGroup().ConfigureAwait(false);
            runner.TryKill(process);
            process.Dispose();
            LauncherLogService.Operation("Assistant session END");
        }
    }
}

/// <summary>Reads newline-delimited messages without letting one unterminated line grow without bound.</summary>
public sealed class BoundedLineReader(TextReader reader, int maxLineChars = 4 * 1024 * 1024)
{
    private readonly char[] _buffer = new char[16 * 1024];
    private int _start;
    private int _end;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_end == 0) return line.Length > 0 ? line.ToString() : null;
            }

            var newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
            var stop = newline < 0 ? _end : newline;
            if (line.Length + (stop - _start) > maxLineChars)
                throw new InvalidDataException("The assistant sent a message larger than the launcher accepts.");
            line.Append(_buffer, _start, stop - _start);
            _start = newline < 0 ? _end : newline + 1;
            if (newline >= 0) return line.ToString().TrimEnd('\r');
        }
    }
}
