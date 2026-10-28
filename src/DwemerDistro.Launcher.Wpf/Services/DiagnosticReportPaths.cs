using System.IO;

namespace DwemerDistro.Launcher.Wpf.Services;

internal static class DiagnosticReportPaths
{
    public static string OutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "DwemerDistro-Diagnostics");

    public static string CreateTimestampedPath(string prefix)
    {
        return Path.Combine(OutputDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    }

    // Keeps browser-delivered reports out of the user's Desktop while they are transferred.
    public static string CreateTemporaryPath(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DwemerDistro", "Diagnostics");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    }
}
