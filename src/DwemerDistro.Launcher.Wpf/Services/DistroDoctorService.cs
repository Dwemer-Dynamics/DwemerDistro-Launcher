using System.Text;
using System.Text.RegularExpressions;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

internal sealed partial class DistroDoctorService(WslService wsl)
{
    public Task<bool> DistroExistsAsync(CancellationToken cancellationToken = default)
    {
        return wsl.DistroExistsAsync(cancellationToken);
    }

    public Task<CommandResult> RunAsync(
        bool repair,
        Action<string> output,
        CancellationToken cancellationToken = default)
    {
        var mode = repair ? "--repair" : "--check";
        var command =
            "if [ -x /usr/local/bin/ddistro_doctor ]; then " +
            $"/usr/local/bin/ddistro_doctor {mode}; " +
            "elif [ -f /home/dwemer/dwemerdistro/bin/ddistro_doctor ]; then " +
            $"bash /home/dwemer/dwemerdistro/bin/ddistro_doctor {mode}; " +
            "else " +
            "echo 'ddistro_doctor is not installed. Falling back to permission helper only.'; " +
            "if [ -x /usr/local/bin/fix_ddistro_permissions ]; then " +
            $"/usr/local/bin/fix_ddistro_permissions {mode}; " +
            "elif [ -f /home/dwemer/dwemerdistro/bin/fix_ddistro_permissions ]; then " +
            $"bash /home/dwemer/dwemerdistro/bin/fix_ddistro_permissions {mode}; " +
            "else echo 'Neither ddistro_doctor nor fix_ddistro_permissions is installed. Run Update System first.'; exit 127; fi; " +
            "fi";

        return wsl.RunBashAsync(
            command,
            output,
            user: "root",
            loginShell: false,
            lineBuffered: true,
            cancellationToken: cancellationToken);
    }

    public static DistroDoctorSummary? ParseFinalSummary(string output)
    {
        var matches = FinalSummaryRegex().Matches(output ?? string.Empty);
        if (matches.Count == 0)
        {
            return null;
        }

        var match = matches[^1];
        return new DistroDoctorSummary(
            int.Parse(match.Groups["checked"].Value),
            int.Parse(match.Groups["repaired"].Value),
            int.Parse(match.Groups["warnings"].Value),
            int.Parse(match.Groups["failed"].Value));
    }

    public static string BuildReport(bool repair, CommandResult result)
    {
        var report = new StringBuilder()
            .AppendLine("DwemerDistro Doctor Report")
            .AppendLine($"Launcher Version: {LauncherConstants.LauncherVersion}")
            .AppendLine($"Generated: {DateTimeOffset.Now}")
            .AppendLine($"Mode: {(repair ? "repair" : "check")}")
            .AppendLine($"Exit Code: {result.ExitCode}")
            .AppendLine()
            .AppendLine("Standard Output")
            .AppendLine(result.StandardOutput.TrimEnd());

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            report
                .AppendLine()
                .AppendLine("Standard Error")
                .AppendLine(result.StandardError.TrimEnd());
        }

        return report.ToString();
    }

    [GeneratedRegex(
        @"Summary:\s+checked=(?<checked>\d+)\s+repaired=(?<repaired>\d+)\s+(?:warnings|issues)=(?<warnings>\d+)\s+failed=(?<failed>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FinalSummaryRegex();
}

internal sealed record DistroDoctorSummary(int Checked, int Repaired, int Warnings, int Failed);
