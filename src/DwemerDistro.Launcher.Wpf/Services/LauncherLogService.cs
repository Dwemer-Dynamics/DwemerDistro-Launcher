using System.IO;

namespace DwemerDistro.Launcher.Wpf.Services;

public static class LauncherLogService
{
    private static readonly object Lock = new();

    public static string StartupLogPath =>
        Path.Combine(AppContext.BaseDirectory, "Logs", "launcher-startup.log");

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
