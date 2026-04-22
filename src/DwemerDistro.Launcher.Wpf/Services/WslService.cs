using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class WslService(ProcessRunner processRunner)
{
    public Task<CommandResult> RunWslAsync(
        IEnumerable<string> arguments,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return processRunner.RunHiddenAsync("wsl.exe", arguments, output, cancellationToken);
    }

    public Task<CommandResult> RunDistroAsync(
        IEnumerable<string> arguments,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(
            new[] { "-d", LauncherConstants.DistroName }.Concat(arguments),
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
            new[] { "-d", LauncherConstants.DistroName, "-u", user, "--" }.Concat(arguments),
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
        var safeCommand = (bashCommand ?? string.Empty).Replace("$", "\\$");
        var shellMode = loginShell ? "-lc" : "-c";
        var shellArgs = lineBuffered
            ? new[] { "stdbuf", "-oL", "-eL", "bash", shellMode, safeCommand }
            : new[] { "bash", shellMode, safeCommand };
        return RunDistroAsUserAsync(user, shellArgs, output, cancellationToken);
    }

    public Task<CommandResult> TerminateDistroAsync(
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "-t", LauncherConstants.DistroName }, output, cancellationToken);
    }

    public Task<CommandResult> UnregisterDistroAsync(
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "--unregister", LauncherConstants.DistroName }, output, cancellationToken);
    }

    public Task<CommandResult> ExportDistroAsync(
        string archivePath,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "--export", LauncherConstants.DistroName, archivePath }, output, cancellationToken);
    }

    public Task<CommandResult> ImportDistroAsync(
        string installPath,
        string archivePath,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunWslAsync(new[] { "--import", LauncherConstants.DistroName, installPath, archivePath }, output, cancellationToken);
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
            .Any(line => string.Equals(line.Trim(), LauncherConstants.DistroName, StringComparison.OrdinalIgnoreCase));
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
}
