using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal static bool IsReignRollbackId(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[0-9]{14}-[0-9]+\z");

    // Core lists only retained artifacts with verified compatibility with the installed database.
    internal static IReadOnlyList<RollbackTarget> ParseReignRollbackTargets(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var targets = new List<RollbackTarget>();
        foreach (var item in document.RootElement.GetProperty("targets").EnumerateArray().Take(50))
        {
            var id = item.GetProperty("id").GetString();
            if (!IsReignRollbackId(id)) continue;
            targets.Add(new RollbackTarget
            {
                Ref = id!, ShaShort = id!, Date = item.GetProperty("date").GetString() ?? "",
                VersionNumber = item.GetProperty("version").GetString() ?? "unknown",
                Label = item.GetProperty("label").GetString() ?? id!
            });
        }
        return targets;
    }

    private async Task OpenReignRollbackWindowAsync()
    {
        if (!ReignManager.CanUseInstalledFeatures) return;
        var result = await _wsl.RunBashAsync("sudo /usr/local/bin/ddistro_reign rollback-list").ConfigureAwait(true);
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not list retained Reign builds. Update DwemerDistro Core and try again.");
        var targets = ParseReignRollbackTargets(result.StandardOutput);
        if (targets.Count == 0)
        {
            MessageBox.Show("No compatible retained ReignServer builds are available yet. Previous builds become available after a verified update. Builds with unknown database compatibility are excluded.",
                "Rollback ReignServer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        using var document = JsonDocument.Parse(result.StandardOutput);
        var current = document.RootElement.GetProperty("current").GetString();
        new RollbackWindow(this, "reign", "ReignServer", "Installed build", current, targets)
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog();
    }

    private async Task RequestReignRollbackAsync(RollbackTarget target, Window window)
    {
        if (!ReignManager.CanUseInstalledFeatures || !IsReignRollbackId(target.Ref)) return;
        if (MessageBox.Show($"Restore ReignServer to:\n\n{target.Label}\n\nThe server will restart. Campaigns, settings and the database remain in place. The current build is retained for recovery.\n\nContinue?",
            "Confirm ReignServer Rollback", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!ReignManager.CanUseInstalledFeatures) return;
        ReignManager.BeginOperation("Rolling back ReignServer...");
        bool succeeded = false;
        try
        {
            var result = await _wsl.RunBashAsync("sudo /usr/local/bin/ddistro_reign rollback " + target.Ref,
                text => AppendLog(text)).ConfigureAwait(true);
            succeeded = result.Succeeded;
            if (succeeded) window.Close();
            else MessageBox.Show("ReignServer rollback failed. Check the launcher console for details.", "Rollback Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception error)
        {
            AppendLog("ReignServer rollback failed: " + error.Message + Environment.NewLine, "red");
        }
        finally
        {
            ReignManager.EndOperation(succeeded ? null : "Last rollback failed");
            await RefreshServerManagementAsync().ConfigureAwait(true);
        }
    }
}
