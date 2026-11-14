using System.Text;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// Clear CHIM request logs, under Database Maintenance. Request logs are troubleshooting records that older CHIM
/// servers copied into every Playthrough Save. Core previews and clears only CHIM's live log and its verified saves;
/// the launcher refuses while a game is open, shows the preview, asks with No as the default, and reports what was
/// actually released.
/// </summary>
public sealed partial class MainWindowViewModel
{
    internal const string ClearChimRequestLogsActionName = "Clearing CHIM request logs";

    public AsyncRelayCommand ClearChimRequestLogsCommand { get; private set; } = null!;

    private void InitializeChimRequestLogCommands()
    {
        ClearChimRequestLogsCommand = new AsyncRelayCommand(
            RunClearChimRequestLogsAsync,
            () => !_systemUpgradeBlocksWrites && CanStartExclusiveDistroOperation());
    }

    internal static string BuildChimRequestLogConfirmation(ChimRequestLogPreview preview)
    {
        var saves = preview.Tables.Where(table => table.Kind == "save").ToList();
        var live = preview.Tables.FirstOrDefault(table => table.Kind == "live");
        var text = new StringBuilder();
        text.Append($"Clear CHIM's request logs? The log tables occupy {FormatByteSize(preview.TotalBytes)}, so clearing frees " +
                    $"up to {FormatByteSize(preview.TotalBytes)} in the database. Each emptied table keeps a small allocation; " +
                    "the actual amount is reported afterwards.\n\n");
        if (live is not null) text.Append($"Current playthrough: {FormatByteSize(live.Bytes)}\n");
        text.Append($"Playthrough Saves: {saves.Count} ({FormatByteSize(saves.Sum(table => table.Bytes))})\n");
        foreach (var save in saves.OrderByDescending(table => table.Bytes).Take(5))
        {
            var name = save.Saves.Count > 0 ? string.Join(", ", save.Saves) : save.Schema;
            text.Append($"  {name}: {FormatByteSize(save.Bytes)}\n");
        }

        if (saves.Count > 5) text.Append($"  and {saves.Count - 5} more\n");
        if (preview.Skipped.Count > 0)
        {
            text.Append($"\n{preview.Skipped.Count} other request log table{(preview.Skipped.Count == 1 ? " is" : "s are")} " +
                        "not a verified CHIM save and will be left alone. The console lists them.\n");
        }

        text.Append("\nRequest logs only record server requests for troubleshooting. Gameplay, memories, characters, " +
                    "settings and the saves themselves are kept, and other mods' databases are not touched. " +
                    "This cannot be undone.\n\nContinue?");
        return text.ToString();
    }

    internal static string BuildChimRequestLogClearedMessage(ChimRequestLogClearResult result) =>
        $"Cleared CHIM request logs in {result.ClearedTables} table{(result.ClearedTables == 1 ? "" : "s")}, actually releasing " +
        $"{FormatByteSize(result.ReclaimedBytes)} in the database" +
        (result.FilesystemFreedBytes > 0 ? $" and {FormatByteSize(result.FilesystemFreedBytes)} on the distro's disk." : ".") +
        " The WSL virtual disk keeps its size on Windows until you run Compact Distro.";

    private async Task RunClearChimRequestLogsAsync()
    {
        const string actionName = ClearChimRequestLogsActionName;
        if (!CanStartExclusiveDistroOperation())
        {
            ReportExclusiveDistroOperationBusy(DatabaseMaintenanceSettingName);
            return;
        }

        if (RefuseWhileSystemUpgradeUnfinished())
        {
            SetDatabaseMaintenanceStatus(SystemUpgradeBlockedMessage, CompactDistroWarningColor);
            return;
        }

        if (!await _wsl.DistroExistsAsync().ConfigureAwait(true))
        {
            SetDatabaseMaintenanceStatus($"{LauncherConstants.DistroName} is not installed. Nothing was changed.", CompactDistroWarningColor);
            MessageBox.Show($"{LauncherConstants.DistroName} is not currently installed.", DatabaseMaintenanceSettingName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A running game keeps writing request logs and saves; refuse before asking and again before clearing.
        if (RefuseDatabaseMaintenanceWhileGameOpen(actionName))
        {
            return;
        }

        if (!TryBeginExclusiveDistroOperation())
        {
            SetDatabaseMaintenanceStatus(ExclusiveDistroOperationBusyMessage, CompactDistroWarningColor);
            ReportExclusiveDistroOperationBusy(DatabaseMaintenanceSettingName);
            return;
        }

        try
        {
            SetDatabaseMaintenanceStatus("Measuring CHIM request logs...", CompactDistroBusyColor);
            if (!await EnsureDatabaseToolAsync(actionName).ConfigureAwait(true))
            {
                return;
            }

            var previewResult = await _wsl.RunDatabaseMaintenanceAsync(ChimRequestLogMaintenance.BuildPreviewArguments(), _ => { })
                .ConfigureAwait(true);
            if (!previewResult.Succeeded)
            {
                ReportChimRequestLogFailure("Could not measure CHIM request logs", previewResult);
                return;
            }

            if (ChimRequestLogMaintenance.ParsePreview(previewResult.StandardOutput) is not { } preview)
            {
                ReportChimRequestLogFailure("Could not measure CHIM request logs",
                    new CommandResult(1, "", "The maintenance tool gave an unrecognized answer."));
                return;
            }

            foreach (var table in preview.Tables)
            {
                AppendLog($"CHIM request logs in {table.Schema} ({(table.Kind == "live" ? "current playthrough" : string.Join(", ", table.Saves))}): " +
                          $"{FormatByteSize(table.Bytes)}{Environment.NewLine}");
            }

            foreach (var skipped in preview.Skipped)
            {
                AppendLog($"Left alone: {skipped.Schema}, {skipped.Reason} ({FormatByteSize(skipped.Bytes)}){Environment.NewLine}", "yellow");
            }

            if (preview.Tables.Count == 0)
            {
                const string nothing = "Nothing to clear. CHIM has no request logs in its live data or verified saves.";
                SetDatabaseMaintenanceStatus(nothing, CompactDistroNeutralColor);
                MessageBox.Show(nothing, DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetDatabaseMaintenanceStatus($"CHIM request log tables occupy {FormatByteSize(preview.TotalBytes)}. Waiting for confirmation...",
                CompactDistroNeutralColor);
            var confirmed = MessageBox.Show(
                BuildChimRequestLogConfirmation(preview),
                "Confirm Clearing CHIM Request Logs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmed != MessageBoxResult.Yes)
            {
                SetDatabaseMaintenanceStatus("Canceled. Nothing was changed.", CompactDistroNeutralColor);
                AppendLog($"{actionName} canceled.{Environment.NewLine}");
                return;
            }

            // Again right before the tool runs: the question can stay open any length of time.
            if (RefuseDatabaseMaintenanceWhileGameOpen(actionName))
            {
                return;
            }

            SetDatabaseMaintenanceStatus("Clearing CHIM request logs...", CompactDistroBusyColor);
            AppendLog($"{actionName}...{Environment.NewLine}");
            var result = await _wsl.RunDatabaseMaintenanceAsync(ChimRequestLogMaintenance.BuildClearArguments(preview.Token), text => AppendLog(text))
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ReportChimRequestLogFailure("Clearing CHIM request logs failed", result);
                return;
            }

            var cleared = ChimRequestLogMaintenance.ParseClear(result.StandardOutput);
            var done = cleared is null
                ? "CHIM request logs were cleared, but the maintenance tool did not report how much space was released."
                : BuildChimRequestLogClearedMessage(cleared);
            AppendLog($"{done}{Environment.NewLine}", "green");
            SetDatabaseMaintenanceStatus($"Done. {done}", CompactDistroSuccessColor);
            MessageBox.Show(done, DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"{actionName} error: {ex.Message}{Environment.NewLine}", "red");
            SetDatabaseMaintenanceStatus("Clearing CHIM request logs could not finish. Open the console for details.", CompactDistroErrorColor);
            MessageBox.Show($"Clearing CHIM request logs could not finish.\n\n{ex.Message}", DatabaseMaintenanceSettingName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetExclusiveDistroOperationInProgress(false);
        }
    }

    /// <summary>The tool's own reason, which says whether anything changed; an older tool gets the Update Distro hint.</summary>
    internal static string DescribeChimRequestLogFailure(CommandResult result) =>
        ChimRequestLogMaintenance.IsUnsupportedByTool(result)
            ? ChimRequestLogMaintenance.ToolTooOldMessage
            : DescribeDatabaseToolFailure(result);

    private void ReportChimRequestLogFailure(string title, CommandResult result)
    {
        var error = DescribeChimRequestLogFailure(result);
        AppendLog($"{title}: {error}{Environment.NewLine}", "red");
        SetDatabaseMaintenanceStatus($"{title}. {FirstLine(error)} Open the console for details.", CompactDistroErrorColor);
        MessageBox.Show($"{title}.\n\n{error}", DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.EndsWith('.') || line.Length == 0 ? line : line + ".";
    }
}
