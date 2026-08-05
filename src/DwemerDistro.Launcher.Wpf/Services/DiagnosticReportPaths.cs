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
}
