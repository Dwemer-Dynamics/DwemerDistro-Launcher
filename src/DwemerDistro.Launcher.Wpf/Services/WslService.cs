using DwemerDistro.Launcher.Wpf.Models;
using Microsoft.Win32;
using System.IO;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <param name="distroName">Only tests pass another name, to run the same commands against an isolated distro.</param>
/// <param name="lxssKeyPath">Only tests pass another HKCU key, to read registrations without touching WSL's own.</param>
public sealed class WslService(
    ProcessRunner processRunner,
    string distroName = LauncherConstants.DistroName,
    string lxssKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss")
{
    public string DistroName { get; } = distroName;

    internal Task<CommandResult> RunDatabaseMaintenanceAsync(
        string[] arguments, Action<string> output, CancellationToken cancellationToken = default)
        => RunWslAsync(["-d", DistroName, "-u", "root", "--exec", .. DistroUpdateRunner.BuildLockedRootArguments(arguments)],
            output, cancellationToken);

    public Task<CommandResult> RunWslAsync(
        IEnumerable<string> arguments,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return processRunner.RunHiddenAsync("wsl.exe", arguments, output, cancellationToken);
    }

    public Task<CommandResult> RunWslWithInputAsync(
        IEnumerable<string> arguments,
        string input,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return processRunner.RunHiddenWithInputAsync("wsl.exe", arguments, input, output, cancellationToken);
    }

    public Task<CommandResult> RunWslWithEnvironmentAsync(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return processRunner.RunHiddenWithEnvironmentAsync("wsl.exe", arguments, environment, output, cancellationToken);
    }

    public Task<CommandResult> RunDistroAsync(
        IEnumerable<string> arguments,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(
            new[] { "-d", DistroName }.Concat(arguments),
            output,
            cancellationToken);
    }

    public Task<CommandResult> RunDistroAsUserAsync(
        string user,
        IEnumerable<string> arguments,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(
            new[] { "-d", DistroName, "-u", user, "--" }.Concat(arguments),
            output,
            cancellationToken);
    }

    public Task<CommandResult> RunDistroAsUserWithInputAsync(
        string user,
        IEnumerable<string> arguments,
        string input,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslWithInputAsync(
            new[] { "-d", DistroName, "-u", user, "--" }.Concat(arguments),
            input,
            output,
            cancellationToken);
    }

    public Task<CommandResult> RunDistroAsUserWithEnvironmentAsync(
        string user,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslWithEnvironmentAsync(
            new[] { "-d", DistroName, "-u", user, "--" }.Concat(arguments),
            environment,
            output,
            cancellationToken);
    }

    public Task<CommandResult> RunBashAsync(
        string bashCommand,
        Action<string>? output = null,
        string user = LauncherConstants.DistroUser,
        bool loginShell = true,
        bool lineBuffered = false,
        CancellationToken cancellationToken = default)
    {
        // wsl.exe can expand unescaped $VARS before bash receives the command.
        // Escape dollar signs so bash sees and expands them inside the distro.
        var normalizedCommand = (bashCommand ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var safeCommand = normalizedCommand.Replace("$", "\\$", StringComparison.Ordinal);
        var shellMode = loginShell ? "-lc" : "-c";
        var shellArgs = lineBuffered
            ? new[] { "stdbuf", "-oL", "-eL", "bash", shellMode, safeCommand }
            : new[] { "bash", shellMode, safeCommand };
        return RunDistroAsUserAsync(user, shellArgs, output, cancellationToken);
    }

    public Task<CommandResult> ShutdownAsync(
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "--shutdown" }, output, cancellationToken);
    }

    public Task<CommandResult> TerminateDistroAsync(
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "-t", DistroName }, output, cancellationToken);
    }

    public Task<CommandResult> UnregisterDistroAsync(
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "--unregister", DistroName }, output, cancellationToken);
    }

    public Task<CommandResult> ExportDistroAsync(
        string archivePath,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "--export", DistroName, archivePath }, output, cancellationToken);
    }

    public Task<CommandResult> ImportDistroAsync(
        string installPath,
        string archivePath,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "--import", DistroName, installPath, archivePath }, output, cancellationToken);
    }

    public async Task<bool> DistroExistsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunWslAsync(new[] { "-l", "-q" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return false;
        }

        var normalizedOutput = result.StandardOutput.Replace("\0", string.Empty);
        return normalizedOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), DistroName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> DistroRunningAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunWslAsync(new[] { "-l", "-q", "--running" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return false;
        }

        var normalizedOutput = result.StandardOutput.Replace("\0", string.Empty);
        return normalizedOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), DistroName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string?> GetWslIpAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunDistroAsync(new[] { "hostname", "-I" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return null;
        }

        return result.StandardOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }

    public string? GetDistroBasePath() => ReadDistroRegistration((_, distroKey) => distroKey.GetValue("BasePath") as string);

    /// <summary>
    /// The WSL registration GUID of the DwemerDistro distro, exactly as WSL names its key; compare it
    /// ignoring case. A reinstall or re-import registers a new one, so it identifies this installation
    /// without asking WSL anything. A key that is not a GUID is no registration id.
    /// </summary>
    public string? GetDistroRegistrationId() =>
        ReadDistroRegistration((subKeyName, _) => Guid.TryParse(subKeyName, out var _) ? subKeyName : null);

    /// <summary>
    /// The WSL 2 registration named <paramref name="distributionName"/>: its GUID, BasePath and VhdFileName exactly as
    /// WSL records them, so the disk is never assumed to be <c>ext4.vhdx</c>. Null when absent, not WSL 2, or incomplete.
    /// </summary>
    internal (string Id, string BasePath, string VhdFileName)? ReadRegistration(string distributionName) =>
        ReadDistroRegistration<(string, string, string)?>(distributionName, (subKeyName, distroKey) =>
            Guid.TryParse(subKeyName, out var _) && distroKey.GetValue("Version") is int and 2
            && distroKey.GetValue("BasePath") is string { Length: > 0 } basePath
            && distroKey.GetValue("VhdFileName") is string { Length: > 0 } vhdFileName
                ? (subKeyName, basePath, vhdFileName)
                : ((string, string, string)?)null);

    private string? ReadDistroRegistration(Func<string, RegistryKey, string?> read) =>
        ReadDistroRegistration<string?>(DistroName, read);

    /// <returns><c>default</c> when no registration has that distribution name.</returns>
    private T ReadDistroRegistration<T>(string distributionNameToFind, Func<string, RegistryKey, T> read)
    {
        using var lxssKey = Registry.CurrentUser.OpenSubKey(lxssKeyPath);
        if (lxssKey is null)
        {
            return default!;
        }

        foreach (var subKeyName in lxssKey.GetSubKeyNames())
        {
            using var distroKey = lxssKey.OpenSubKey(subKeyName);
            if (distroKey is null)
            {
                continue;
            }

            var distributionName = distroKey.GetValue("DistributionName") as string;
            if (!string.Equals(distributionName, distributionNameToFind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return read(subKeyName, distroKey);
        }

        return default!;
    }

    public string? GetDistroVhdxPath()
    {
        var basePath = GetDistroBasePath();
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(basePath, "ext4.vhdx"),
                     Path.Combine(basePath, "LocalState", "ext4.vhdx")
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
