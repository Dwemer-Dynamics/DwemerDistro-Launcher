using System.IO;
using Microsoft.Win32;

namespace DwemerDistro.Launcher.Wpf.Services;

internal static class DiagnosticProtocolRegistrationService
{
    internal const string Scheme = "dwemerdistro";

    // Registers the browser link used by local server pages to request the existing support report.
    public static void EnsureRegistered()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.GetFileName(executablePath).Equals("DwemerDistro.exe", StringComparison.OrdinalIgnoreCase))
        {
            LauncherLogService.Startup("Diagnostic browser protocol registration skipped outside the published launcher.");
            return;
        }

        using var protocolKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        protocolKey.SetValue(null, "URL:DwemerDistro Diagnostic Protocol");
        protocolKey.SetValue("URL Protocol", string.Empty);

        using var iconKey = protocolKey.CreateSubKey("DefaultIcon");
        iconKey.SetValue(null, $"\"{executablePath}\",0");

        using var commandKey = protocolKey.CreateSubKey(@"shell\open\command");
        commandKey.SetValue(null, BuildOpenCommand(executablePath));
    }

    internal static string BuildOpenCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"'))
        {
            throw new ArgumentException("A valid launcher executable path is required.", nameof(executablePath));
        }

        return $"\"{executablePath}\" --download-diagnostics \"%1\"";
    }
}
