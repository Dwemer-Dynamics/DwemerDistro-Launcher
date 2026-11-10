namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>Owns the Linux update process group so cancellation also stops privileged descendants.</summary>
internal static class DistroUpdateRunner
{
    /// <summary>Held by whichever launcher's update is running, including one left behind by a launcher that closed.</summary>
    internal const string LockPath = "/home/dwemer/.dwemerdistro-launcher-update.lock";

    /// <summary>The exit code when another update holds <see cref="LockPath"/>; nothing was changed.</summary>
    internal const int OtherUpdateRunningExitCode = 75;

    /// <summary>
    /// Waits, as dwemer, until no update holds the lock, bounded by the update's own 30 minute limit. It only reads
    /// the lock, holding it shared only while <c>true</c> runs, and a missing lock file means no update ever ran.
    /// </summary>
    internal static string BuildWaitForOtherUpdateCommand() =>
        $"[ -e {LockPath} ] || exit 0; exec timeout 1840 flock -s -w 1830 {LockPath} true";

    internal static async Task<Models.CommandResult> RunAsync(
        WslService wsl, string command, Action<string> output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pidFile = "/run/dwemerdistro-update-" + Guid.NewGuid().ToString("N") + ".pid";
        var quotedCommand = "'" + command.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        // Only the process supervisor runs as root. Git, pip and checkout ownership stay with dwemer.
        var script = $"umask 077\nprintf '%s\\n' \"$$\" > {pidFile} || exit 1\n" +
            "exec timeout --kill-after=10s 30m runuser -u dwemer -- " +
            $"flock -n -E {OtherUpdateRunningExitCode} --close {LockPath} " +
            $"bash -c {quotedCommand} </dev/null\n";
        try
        {
            var result = await wsl.RunDistroAsUserWithInputAsync("root", ["setsid", "--wait", "bash", "-s"],
                script, output, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == OtherUpdateRunningExitCode) output("Another distro update is already running.\n");
            return result;
        }
        finally
        {
            // wsl.exe termination alone does not reliably terminate processes inside the distro.
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var cleanup = (cancellationToken.IsCancellationRequested
                ? $"for attempt in 1 2 3 4 5; do [ -s {pidFile} ] && break; sleep 0.2; done; " : "") +
                $"if [ -s {pidFile} ]; then " +
                $"read -r pid < {pidFile}; " +
                "case \"$pid\" in ''|*[!0-9]*) exit 1;; esac; " +
                "if kill -0 -- -\"$pid\" 2>/dev/null; then " +
                "kill -TERM -- -\"$pid\" 2>/dev/null; sleep 1; " +
                "kill -KILL -- -\"$pid\" 2>/dev/null || true; fi; " +
                $"rm -f {pidFile}; fi";
            var cleanupResult = await wsl.RunBashAsync(cleanup, user: "root", loginShell: false,
                cancellationToken: cleanupTimeout.Token).ConfigureAwait(false);
            if (!cleanupResult.Succeeded)
                throw new InvalidOperationException("Could not stop the distro update process group.");
        }
    }
}
