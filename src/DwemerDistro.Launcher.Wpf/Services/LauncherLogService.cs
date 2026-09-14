using System.IO;

namespace DwemerDistro.Launcher.Wpf.Services;

public static class LauncherLogService
{
    private static readonly object Lock = new();

    public static string StartupLogPath =>
        Path.Combine(AppContext.BaseDirectory, "Logs", "launcher-startup.log");

    // Retain bounded install/update output across launcher restarts, without logging game traffic.
    public static void Operation(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Logs", "launcher-operations.log");
            var text = DiagnosticEvidenceService.Sanitize(message);
            if (text.Length > 65536) text = "[truncated chunk] " + text[^65536..];
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "launcher-operations.previous.log"), overwrite: true);
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {text.TrimEnd()}{Environment.NewLine}");
            }
        }
        catch
        {
            // Evidence must not change an operation's outcome when logging fails.
        }
    }

    public static void Startup(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            lock (Lock)
            {
                File.AppendAllText(StartupLogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never become a startup dependency.
        }
    }
}
