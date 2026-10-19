using System.Windows;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// Optional application-server management for the Mods page: status, install, repair and the guarded
/// uninstall. All of it goes through <see cref="ServerManagementService"/>, so no product or branch
/// string reaches a shell.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Separates the distro core update from the shared components update in the console.</summary>
    private const string SharedComponentsMarker = "=====MARKER:BEGIN_SHARED_COMPONENTS=====";

    private ServerManagementService? _serverManagement;

    public ServerManagerItemViewModel HerikaManager { get; private set; } = null!;

    public ServerManagerItemViewModel StobeManager { get; private set; } = null!;

    public ServerManagerItemViewModel DialecticManager { get; private set; } = null!;

    public ServerManagerItemViewModel LorkhanManager { get; private set; } = null!;

    public ServerManagerItemViewModel ReignManager { get; private set; } = null!;

    /// <summary>The three products in rail order. Backs status refresh and every mod update.</summary>
    public IReadOnlyList<ServerManagerItemViewModel> ServerManagers { get; private set; } = [];

    private void InitializeServerManagement()
    {
        _serverManagement = new ServerManagementService(_wsl);

        HerikaManager = CreateServerManagerItem(ServerProduct.Herika, "CHIM");
        StobeManager = CreateServerManagerItem(ServerProduct.Stobe, "STOBE");
        DialecticManager = CreateServerManagerItem(ServerProduct.Dialectic, "DIALECTIC");
        ReignManager = CreateServerManagerItem(ServerProduct.Reign, "REIGN");
        LorkhanManager = CreateServerManagerItem(ServerProduct.Lorkhan, "LORKHAN");
        ServerManagers = [HerikaManager, LorkhanManager, DialecticManager, StobeManager, ReignManager];

        // The three items live for the window's lifetime, so watching each one's busy flag needs no
        // detach: it is how one product's running operation disables the others' single-product
        // update without any item having to know about its siblings.
        foreach (var manager in ServerManagers)
        {
            manager.PropertyChanged += OnServerManagerPropertyChanged;
        }
    }

    private void OnServerManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ReignManager) && e.PropertyName == nameof(ServerManagerItemViewModel.SelectedBranch))
        {
            QueueBackgroundTask("Reign version check", CheckReignServerUpdatesAsync, StartupVersionCheckTimeout);
        }
        if (e.PropertyName == nameof(ServerManagerItemViewModel.IsBusy))
        {
            RefreshServerUpdateConflictState();
            RaiseUpdateCommandStates();
            OnPropertyChanged(nameof(IsComponentInteractionEnabled));
        }

        if (e.PropertyName == nameof(ServerManagerItemViewModel.CanUseInstalledFeatures))
        {
            OpenChimCommand?.RaiseCanExecuteChanged();
            OpenStobeCommand?.RaiseCanExecuteChanged();
            OpenDialecticCommand?.RaiseCanExecuteChanged();
            OpenReignCommand?.RaiseCanExecuteChanged();
            OpenLorkhanCommand?.RaiseCanExecuteChanged();
            OpenHerikaRollbackCommand?.RaiseCanExecuteChanged();
            OpenStobeRollbackCommand?.RaiseCanExecuteChanged();
            OpenDialecticRollbackCommand?.RaiseCanExecuteChanged();
            OpenReignRollbackCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Keeps server actions out of the way while any server, component or system operation runs.
    /// </summary>
    internal void RefreshServerUpdateConflictState()
    {
        var busy = IsDistroUpdateInProgress ||
                   _isComponentsOperationInProgress ||
                   IsCriticalMaintenanceInProgress ||
                   _isCustomModMutationInProgress ||
                   ServerManagers.Any(manager => manager.IsBusy);
        foreach (var manager in ServerManagers)
        {
            manager.IsConflictingOperationRunning = busy;
        }
    }

    private ServerManagerItemViewModel CreateServerManagerItem(ServerProduct product, string gameKey)
    {
        return new ServerManagerItemViewModel(
            product,
            gameKey,
            InstallServerAsync,
            UpdateServerAsync,
            RepairServerAsync,
            UninstallServerAsync);
    }

    public ServerManagerItemViewModel? FindServerManager(string? gameKey)
    {
        var product = ServerManagementService.TryParseGameKey(gameKey);
        return product is null
            ? null
            : ServerManagers.FirstOrDefault(manager => manager.Product == product.Value);
    }

    /// <summary>
    /// Looks a product up by its manager token ("herika", "stobe", "dialectic"), which is also the
    /// rollback config key. Matching on the token rather than a display string keeps diagnostics
    /// working if a display name is ever reworded.
    /// </summary>
    private ServerManagerItemViewModel? FindServerManagerByKey(string? productKey)
    {
        var product = ServerManagementService.ParseProduct(productKey);
        return product is null
            ? null
            : ServerManagers.FirstOrDefault(manager => manager.Product == product.Value);
    }

    /// <summary>
    /// Re-reads <c>status all --json</c> and pushes it into the three items. A failed probe marks each
    /// item as unavailable, rather than claiming everything is missing.
    /// </summary>
    private async Task RefreshServerManagementAsync(CancellationToken cancellationToken = default)
    {
        var service = _serverManagement;
        if (service is null)
        {
            return;
        }

        ServerStatusResult result;
        try
        {
            result = await service.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A timed-out or cancelled probe must not leave the items stuck on "Checking".
            RunOnUi(MarkServerStatusUnavailable);
            throw;
        }

        RunOnUi(() =>
        {
            if (!result.IsSuccess)
            {
                MarkServerStatusUnavailable();
                return;
            }

            foreach (var manager in ServerManagers)
            {
                manager.ApplyStatus(result.Snapshot!.Find(manager.Product));
            }

            RaiseServerManagerDependentStates();
        });
        if (result.IsSuccess && ReignManager.IsInstalled)
            QueueBackgroundTask("Reign version check", CheckReignServerUpdatesAsync, StartupVersionCheckTimeout);
        if (result.IsSuccess && LorkhanManager.IsInstalled)
            QueueBackgroundTask("Lorkhan version details", RefreshLorkhanVersionDetailsAsync, StartupVersionCheckTimeout);
    }

    private void MarkServerStatusUnavailable()
    {
        foreach (var manager in ServerManagers)
        {
            manager.ApplyStatusError("Server status unavailable");
        }

        RaiseServerManagerDependentStates();
    }

    // Lorkhan keeps a semantic version file; its installed Git revision supplies the build date.
    private async Task RefreshLorkhanVersionDetailsAsync(CancellationToken cancellationToken = default)
    {
        var branch = LorkhanManager.InstalledBranch;
        var installed = LorkhanManager.InstalledVersion;
        if (!LorkhanManager.IsInstalled) return;

        var dateResult = await _wsl.RunBashAsync(
            "git -c safe.directory=/opt/dwemerdistro/sources/LorkhanServer " +
            "-C /opt/dwemerdistro/sources/LorkhanServer log -1 --format=%cd --date=format:%Y%m%d",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var date = dateResult.Succeeded ? dateResult.StandardOutput.Trim() : null;
        if (date is not { Length: 8 } || !date.All(char.IsAsciiDigit)) date = null;
        RunOnUi(() =>
        {
            if (!LorkhanManager.IsInstalled || LorkhanManager.InstalledBranch != branch || LorkhanManager.InstalledVersion != installed) return;
            LorkhanManager.ApplyVersionStatus(
                BuildServerVersionStatusText("lorkhan", branch, FormatDateVersion(date), installed),
                string.IsNullOrWhiteSpace(installed) || date is null ? "Yellow" : "LimeGreen", false);
        });
    }

    // Read the activated artifact so rollback and failed builds cannot report uninstalled source metadata.
    private async Task CheckReignServerUpdatesAsync(CancellationToken cancellationToken = default)
    {
        string branch = ServerManagementService.ToBranchToken(ReignManager.SelectedBranchChannel);
        string? installed = ReignManager.InstalledVersion;
        if (!ReignManager.IsInstalled) return;

        var dateVersion = await ReadWslFileFirstLineAsync("/var/www/html/ReignServer/runtime/current/.version.txt", cancellationToken).ConfigureAwait(false);
        var semanticVersion = await ReadWslFileFirstLineAsync("/var/www/html/ReignServer/runtime/current/.version_number.txt", cancellationToken).ConfigureAwait(false) ?? installed;
        RunOnUi(() =>
        {
            if (!ReignManager.IsInstalled || ReignManager.InstalledVersion != installed || ServerManagementService.ToBranchToken(ReignManager.SelectedBranchChannel) != branch) return;
            ReignManager.ApplyVersionStatus(
                BuildServerVersionStatusText("reign", branch, FormatDateVersion(dateVersion), semanticVersion),
                string.IsNullOrWhiteSpace(semanticVersion) && string.IsNullOrWhiteSpace(dateVersion) ? "Yellow" : "LimeGreen", false);
        });

        var remoteDate = await GetTextOrNullAsync($"https://raw.githubusercontent.com/Dwemer-Dynamics/ReignServer/{branch}/.version.txt", cancellationToken).ConfigureAwait(false);
        var remoteVersion = await GetTextOrNullAsync($"https://raw.githubusercontent.com/Dwemer-Dynamics/ReignServer/{branch}/.version_number.txt", cancellationToken).ConfigureAwait(false);
        bool updateAvailable = IsReignUpdateAvailable(dateVersion, semanticVersion, remoteDate, remoteVersion);
        RunOnUi(() =>
        {
            if (!ReignManager.IsInstalled || ReignManager.InstalledVersion != installed || ServerManagementService.ToBranchToken(ReignManager.SelectedBranchChannel) != branch) return;
            ReignManager.ApplyVersionStatus(
                BuildServerVersionStatusText("reign", branch, FormatDateVersion(dateVersion), semanticVersion, updateAvailable),
                updateAvailable || (string.IsNullOrWhiteSpace(semanticVersion) && string.IsNullOrWhiteSpace(dateVersion)) ? "Yellow" : "LimeGreen", updateAvailable);
        });
    }

    // A newer dated build on the same version is an update, but an older release is never an upgrade.
    internal static bool IsReignUpdateAvailable(string? installedDate, string? installedVersion, string? remoteDate, string? remoteVersion)
    {
        if (!Version.TryParse(installedVersion?.Trim(), out var current) || !Version.TryParse(remoteVersion?.Trim(), out var available)) return false;
        if (available != current) return available > current;
        return long.TryParse(installedDate, out var currentStamp) && long.TryParse(remoteDate, out var availableStamp) && availableStamp > currentStamp;
    }

    private void RaiseServerManagerDependentStates()
    {
        OnPropertyChanged(nameof(IsComponentInteractionEnabled));
        RaiseUpdateCommandStates();
        RefreshServerUpdateConflictState();
        OpenChimCommand.RaiseCanExecuteChanged();
        OpenStobeCommand.RaiseCanExecuteChanged();
        OpenDialecticCommand.RaiseCanExecuteChanged();
        OpenReignCommand.RaiseCanExecuteChanged();
        OpenHerikaRollbackCommand.RaiseCanExecuteChanged();
        OpenStobeRollbackCommand.RaiseCanExecuteChanged();
        OpenDialecticRollbackCommand.RaiseCanExecuteChanged();
        OpenReignRollbackCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Feeds the existing version status into the matching product's status line.</summary>
    private void ApplyVersionStatusToManager(
        ServerProduct product,
        string text,
        string color,
        bool updateAvailable)
    {
        var manager = ServerManagers.FirstOrDefault(item => item.Product == product);
        manager?.ApplyVersionStatus(text, color, updateAvailable);
    }

    private async Task InstallServerAsync(ServerManagerItemViewModel item)
    {
        if (!item.CanInstall)
        {
            return;
        }

        await RunServerOperationAsync(
                item,
                ServerOperation.Install,
                item.SelectedBranchChannel)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Updates the shared system first, then this product on its selected branch. Other products
    /// are left alone.
    /// </summary>
    private async Task UpdateServerAsync(ServerManagerItemViewModel item)
    {
        if (!item.CanUpdate)
        {
            return;
        }

        var updates = SnapshotModUpdates([item]);
        // Capture the destructive setting with the branch selection shown in this confirmation.
        // A user may change Settings while the system stage runs, but that must affect only the
        // next update rather than changing what this already-confirmed operation will do.
        var forceGitUpdates = ForceGitUpdatesEnabled;
        if (MessageBox.Show(
                BuildModsUpdateConfirmation(updates, forceGitUpdates),
                item.UpdateActionName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            AppendLog("Mod update canceled." + Environment.NewLine);
            return;
        }

        await RunModUpdatesAsync(updates, forceGitUpdates).ConfigureAwait(true);
    }

    private async Task RepairServerAsync(ServerManagerItemViewModel item)
    {
        if (!item.CanRepair)
        {
            return;
        }

        await RunServerOperationAsync(
                item,
                ServerOperation.Repair,
                item.SelectedBranchChannel)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Runs install, update or repair for one product, streaming manager output to the console and always
    /// refreshing status afterwards so a partial failure cannot leave a stale "Installed" label.
    /// </summary>
    private async Task<ServerOperationOutcome> RunServerOperationAsync(
        ServerManagerItemViewModel item,
        ServerOperation operation,
        ServerBranchChannel branch,
        bool forceGitUpdates = false,
        CancellationToken cancellationToken = default)
    {
        var service = _serverManagement;
        if (service is null)
        {
            return ServerOperationOutcome.Failed;
        }

        var (progressVerb, pastVerb, failureVerb) = DescribeOperation(operation);
        var branchChoice = ServerManagementService.ToBranchChoice(branch);

        RunOnUi(() => item.BeginOperation($"{progressVerb} ({branchChoice})..."));
        AppendLog(
            $"{Environment.NewLine}{progressVerb} {item.DisplayName} on the {branchChoice} branch...{Environment.NewLine}",
            "green");

        var outcome = ServerOperationOutcome.Failed;
        try
        {
            if (operation == ServerOperation.Update)
            {
                // Only update can force: the setting exists so a dirty worktree stops blocking an
                // update, and install and repair have no confirmed edits to discard.
                outcome = await UpdateWithOneTimeConsentAsync(
                        forceGitUpdates,
                        force =>
                        {
                            if (force && !forceGitUpdates)
                            {
                                AppendLog($"Discarding local edits and retrying the {item.DisplayName} update once...{Environment.NewLine}", "yellow");
                            }

                            return service.UpdateAsync(item.Product, branch, force, line => AppendLog(line), cancellationToken);
                        },
                        blocker => ConfirmDiscardTrackedEdits(item.DisplayName, blocker),
                        blocker =>
                        {
                            AppendLog($"{item.DisplayName} update failed: untracked files are in the way.{Environment.NewLine}", "red");
                            ShowUntrackedUpdateBlocker(item.DisplayName, blocker);
                        },
                        failed =>
                        {
                            var error = GetCommandError(failed);
                            AppendLog($"{item.DisplayName} update failed: {error}{Environment.NewLine}", "red");
                            ShowServerUpdateFailure(item.DisplayName, error);
                        })
                    .ConfigureAwait(false);
                if (outcome == ServerOperationOutcome.Succeeded)
                {
                    AppendLog($"{item.DisplayName} {pastVerb} successfully.{Environment.NewLine}", "green");
                }
                else if (outcome == ServerOperationOutcome.Skipped)
                {
                    AppendLog($"{item.DisplayName} update skipped. Local edits were kept.{Environment.NewLine}", "yellow");
                }
            }
            else
            {
                var result = operation == ServerOperation.Install
                    ? await service
                        .InstallAsync(item.Product, branch, line => AppendLog(line), cancellationToken)
                        .ConfigureAwait(false)
                    : await service
                        .RepairAsync(item.Product, branch, line => AppendLog(line), cancellationToken)
                        .ConfigureAwait(false);

                outcome = result.Succeeded ? ServerOperationOutcome.Succeeded : ServerOperationOutcome.Failed;
                AppendLog(
                    result.Succeeded
                        ? $"{item.DisplayName} {pastVerb} successfully.{Environment.NewLine}"
                        : $"{item.DisplayName} {failureVerb} failed: {GetCommandError(result)}{Environment.NewLine}",
                    result.Succeeded ? "green" : "red");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{item.DisplayName} {failureVerb} was canceled.{Environment.NewLine}", "yellow");
        }
        catch (Exception ex)
        {
            AppendLog($"{item.DisplayName} {failureVerb} failed: {ex.Message}{Environment.NewLine}", "red");
            if (operation == ServerOperation.Update)
            {
                ShowServerUpdateFailure(item.DisplayName, ex.Message);
            }
        }
        finally
        {
            var status = outcome switch
            {
                ServerOperationOutcome.Succeeded => null,
                ServerOperationOutcome.Skipped => "Update skipped: local edits kept",
                _ => $"Last {failureVerb} failed"
            };
            RunOnUi(() => item.EndOperation(status));
            await RefreshServerManagementSafeAsync().ConfigureAwait(false);
            QueueServerVersionRefresh(item.Product);
        }

        return outcome;
    }

    /// <summary>
    /// Runs one mod update. Only when the manager reports tracked edits as the blocker and Force
    /// Updates was off does it ask, once, whether to discard them, and then retries this mod once
    /// with --force. Consent comes only from that answer; cancellation propagates with no retry.
    /// </summary>
    internal static async Task<ServerOperationOutcome> UpdateWithOneTimeConsentAsync(
        bool forceGitUpdates,
        Func<bool, Task<CommandResult>> runUpdate,
        Func<ServerUpdateBlocker, bool> confirmDiscard,
        Action<ServerUpdateBlocker> reportUntrackedBlocker,
        Action<CommandResult> reportFailure)
    {
        var result = await runUpdate(forceGitUpdates).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return ServerOperationOutcome.Succeeded;
        }

        var blocker = ServerManagementService.TryParseUpdateBlocker(result);
        // Consent needs named files: a tracked blocker without any is reported as a plain failure.
        if (blocker is null || forceGitUpdates || !blocker.IsTrackedEdits || blocker.Files.Count == 0)
        {
            return ReportUpdateFailure(result, blocker);
        }

        if (!confirmDiscard(blocker))
        {
            return ServerOperationOutcome.Skipped;
        }

        // A failed retry is final: it is reported, never followed by another discard prompt.
        var retry = await runUpdate(true).ConfigureAwait(false);
        return retry.Succeeded
            ? ServerOperationOutcome.Succeeded
            : ReportUpdateFailure(retry, ServerManagementService.TryParseUpdateBlocker(retry));

        ServerOperationOutcome ReportUpdateFailure(CommandResult failed, ServerUpdateBlocker? failedBlocker)
        {
            if (failedBlocker is { IsTrackedEdits: false })
            {
                reportUntrackedBlocker(failedBlocker);
            }
            else
            {
                reportFailure(failed);
            }

            return ServerOperationOutcome.Failed;
        }
    }

    internal static string BuildTrackedEditsBlockedMessage(string displayName, ServerUpdateBlocker blocker)
    {
        return $"Local edits are blocking the {displayName} update.\n\n" +
               $"Edited files:\n{FormatBlockedFiles(blocker)}\n\n" +
               $"Yes: discard your local edits to tracked {displayName} files and retry this update once. " +
               "Git may also replace conflicting untracked or ignored files. This cannot be undone, " +
               "so back up custom files first.\n\n" +
               "No: keep your edits and skip this mod update.\n\n" +
               "DwemerDistro and shared components were already updated.";
    }

    internal static string BuildUntrackedFilesBlockedMessage(string displayName, ServerUpdateBlocker blocker)
    {
        return $"{displayName} was not updated. These files are not part of the server, but new server files need their place:\n\n" +
               $"{FormatBlockedFiles(blocker)}\n\n" +
               "Move or rename them, then update again.";
    }

    internal static string BuildServerUpdateFailureMessage(string displayName, string details)
    {
        // The console keeps the full output; the dialog only needs the tail that explains it.
        const int maxLines = 12;
        const int maxCharacters = 1200;
        var lines = details.Trim().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - maxLines)));
        if (tail.Length > maxCharacters)
        {
            tail = "..." + tail[^maxCharacters..];
        }

        return $"{displayName} update failed.\n\n{tail}\n\nThe console has the full output.";
    }

    // A native message box grows with its text, so the list is capped in both directions to keep
    // the buttons on screen. The console has Git's complete list.
    internal const int BlockedFilesShownLimit = 6;
    internal const int BlockedFileShownLength = 100;

    private static string FormatBlockedFiles(ServerUpdateBlocker blocker)
    {
        var lines = blocker.Files
            .Take(BlockedFilesShownLimit)
            .Select(file => "  " + (file.Length <= BlockedFileShownLength
                ? file
                : "..." + file[^(BlockedFileShownLength - 3)..]))
            .ToList();
        if (lines.Count == 0)
        {
            return "  (see the console for the file list)";
        }

        var hidden = Math.Max(blocker.TotalFiles, blocker.Files.Count) - lines.Count;
        if (hidden > 0)
        {
            lines.Add($"  ...and {hidden} more");
        }

        return string.Join("\n", lines);
    }

    internal static bool ConfirmDiscardTrackedEdits(string displayName, ServerUpdateBlocker blocker)
    {
        return ShowOwnedServerMessage(
                   BuildTrackedEditsBlockedMessage(displayName, blocker),
                   $"{displayName} update blocked",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    internal static void ShowUntrackedUpdateBlocker(string displayName, ServerUpdateBlocker blocker)
    {
        ShowOwnedServerMessage(
            BuildUntrackedFilesBlockedMessage(displayName, blocker),
            $"{displayName} update blocked",
            MessageBoxButton.OK,
            MessageBoxImage.Warning,
            MessageBoxResult.OK);
    }

    internal static void ShowServerUpdateFailure(string displayName, string details)
    {
        ShowOwnedServerMessage(
            BuildServerUpdateFailureMessage(displayName, details),
            $"{displayName} update failed",
            MessageBoxButton.OK,
            MessageBoxImage.Error,
            MessageBoxResult.OK);
    }

    /// <summary>
    /// Shows a message box owned by the main window on the UI thread. Without a running application
    /// it shows nothing and returns the default, so no answer is ever invented as consent.
    /// </summary>
    private static MessageBoxResult ShowOwnedServerMessage(
        string text,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return defaultResult;
        }

        return dispatcher.Invoke(() =>
        {
            var owner = Application.Current?.MainWindow;
            return owner is { IsVisible: true }
                ? MessageBox.Show(owner, text, caption, buttons, image, defaultResult)
                : MessageBox.Show(text, caption, buttons, image, defaultResult);
        });
    }

    /// <summary>
    /// Destructive purge. Refuses while the distro is running or starting, requires the typed PURGE
    /// token, and refreshes status on both success and failure.
    /// </summary>
    private async Task UninstallServerAsync(ServerManagerItemViewModel item)
    {
        var service = _serverManagement;
        if (service is null || !item.CanUninstall)
        {
            return;
        }

        if (IsServerBusyForUninstall(item))
        {
            return;
        }

        if (!await ConfirmUninstallAsync(item).ConfigureAwait(true))
        {
            AppendLog($"{item.DisplayName} uninstall canceled.{Environment.NewLine}");
            return;
        }

        if (!item.CanUninstall || IsServerBusyForUninstall(item))
        {
            return;
        }

        RunOnUi(() => item.BeginOperation("Uninstalling..."));
        AppendLog($"{Environment.NewLine}Uninstalling {item.DisplayName}...{Environment.NewLine}", "green");

        var succeeded = false;
        try
        {
            var result = await service.UninstallAsync(item.Product, line => AppendLog(line)).ConfigureAwait(false);
            succeeded = result.Succeeded;
            AppendLog(
                succeeded
                    ? $"{item.DisplayName} was uninstalled.{Environment.NewLine}"
                    : $"{item.DisplayName} uninstall failed: {GetCommandError(result)}{Environment.NewLine}",
                succeeded ? "green" : "red");
        }
        catch (Exception ex)
        {
            AppendLog($"{item.DisplayName} uninstall failed: {ex.Message}{Environment.NewLine}", "red");
        }
        finally
        {
            RunOnUi(() => item.EndOperation(succeeded ? null : "Last uninstall failed"));
            await RefreshServerManagementSafeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when the uninstall must not proceed. Offers the existing Stop Server flow instead of
    /// stopping anything on its own.
    /// </summary>
    private bool IsServerBusyForUninstall(ServerManagerItemViewModel item)
    {
        // WSL itself must be running for ddistro_server to perform the uninstall. Only the managed
        // application-server session is a conflict; an idle distro is the expected command host.
        if (!IsServerRunning && !IsServerStarting)
        {
            return false;
        }

        AppendLog(
            $"{item.DisplayName} uninstall blocked: DwemerDistro is running or starting.{Environment.NewLine}",
            "yellow");

        var stopNow = MessageBox.Show(
            $"DwemerDistro is running or starting, so {item.DisplayName} cannot be uninstalled yet.\n\n" +
            "Stop the server now? Uninstall stays available once it has stopped.",
            $"Uninstall {item.DisplayName}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (stopNow == MessageBoxResult.Yes && StopServerCommand.CanExecute(null))
        {
            StopServerCommand.Execute(null);
        }

        return true;
    }

    private async Task<bool> ConfirmUninstallAsync(ServerManagerItemViewModel item)
    {
        var confirmation = await _dispatcher.InvokeAsync(() =>
        {
            var window = new ServerUninstallWindow(item.Product, item.RailProductName)
            {
                Owner = Application.Current?.MainWindow
            };
            return window.ShowDialog() == true;
        }).Task.ConfigureAwait(true);

        return confirmation;
    }

    private async Task RefreshServerManagementSafeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(StartupVersionCheckTimeout);
            await RefreshServerManagementAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Server status refresh timed out. It will retry shortly." + Environment.NewLine, "yellow");
        }
        catch (Exception ex)
        {
            AppendLog($"Server status refresh failed: {ex.Message}{Environment.NewLine}", "yellow");
        }
    }

    private void QueueServerVersionRefresh(ServerProduct product)
    {
        switch (product)
        {
            case ServerProduct.Herika:
                QueueBackgroundTask("Herika version check", CheckForUpdatesAsync, StartupVersionCheckTimeout);
                break;
            case ServerProduct.Stobe:
                QueueBackgroundTask("Stobe version check", CheckStobeServerUpdatesAsync, StartupVersionCheckTimeout);
                break;
            case ServerProduct.Dialectic:
                QueueBackgroundTask("Dialectic version check", CheckDialecticServerUpdatesAsync, StartupVersionCheckTimeout);
                break;
        }
    }

    // --- Mod updates -----------------------------------------------------------------------

    /// <summary>
    /// A product is updated only when it is really installed. Update must never install a missing
    /// or broken product, so a not-installed, needs-repair or unknown state is always excluded.
    /// This is the single guard every mod update path funnels through.
    /// </summary>
    internal static bool ShouldUpdateProduct(ServerInstallState state)
    {
        return state == ServerInstallState.Installed;
    }

    /// <summary>
    /// The shared distro/components update always skips all three application servers: the server
    /// manager owns their repositories now, so update_gws only refreshes shared services.
    /// </summary>
    internal static string BuildSharedComponentsUpdateCommand()
    {
        return "/usr/local/bin/update_gws --skip-herika --skip-stobe --skip-dialectic --skip-reign --skip-lorkhan";
    }

    /// <summary>
    /// Captures the exact branches displayed by the confirmation, before its modal dispatcher runs.
    /// </summary>
    internal static IReadOnlyList<(ServerManagerItemViewModel Product, ServerBranchChannel Branch)> SnapshotModUpdates(
        IReadOnlyList<ServerManagerItemViewModel> products)
    {
        return products
            .Where(product => ShouldUpdateProduct(product.State))
            .Select(product => (Product: product, Branch: product.SelectedBranchChannel))
            .ToArray();
    }

    /// <summary>
    /// Rechecks eligibility without changing the confirmed branches. The shared system update always
    /// runs first - so a distro whose mods are missing or whose status is unreadable still gets its
    /// core and shared components repaired - and its failure stops the batch, while an individual mod
    /// failure still allows the remaining mods. An empty eligible selection is a successful
    /// system-only update, never an error.
    /// </summary>
    /// <param name="refreshUpdates">
    /// Optional re-read of server status after the system stage. Recovery is the reason it exists: a
    /// mod that only became visible once the system update restored <c>ddistro_server</c> is still
    /// updated, on the branch the user confirmed when it was already known.
    /// </param>
    internal static async Task<bool> UpdateInstalledServersAsync(
        IReadOnlyList<(ServerManagerItemViewModel Product, ServerBranchChannel Branch)> confirmedUpdates,
        Func<Task<bool>> updateSystem,
        Func<ServerManagerItemViewModel, ServerBranchChannel, Task<bool>> updateMod,
        Func<Task<IReadOnlyList<(ServerManagerItemViewModel Product, ServerBranchChannel Branch)>>>? refreshUpdates = null)
    {
        if (!await updateSystem().ConfigureAwait(true))
        {
            return false;
        }

        var candidates = refreshUpdates is null
            ? confirmedUpdates
            : MergeConfirmedBranches(confirmedUpdates, await refreshUpdates().ConfigureAwait(true));

        var updates = candidates
            .Where(update => ShouldUpdateProduct(update.Product.State))
            .ToArray();

        var allSucceeded = true;
        foreach (var update in updates)
        {
            var succeeded = await updateMod(update.Product, update.Branch).ConfigureAwait(true);
            allSucceeded &= succeeded;
        }

        return allSucceeded;
    }

    /// <summary>
    /// Keeps the branch the user already approved for every product that was part of the
    /// confirmation, and accepts the currently selected branch only for products the post-system
    /// refresh discovered, which the confirmation could not have named.
    /// </summary>
    internal static IReadOnlyList<(ServerManagerItemViewModel Product, ServerBranchChannel Branch)> MergeConfirmedBranches(
        IReadOnlyList<(ServerManagerItemViewModel Product, ServerBranchChannel Branch)> confirmedUpdates,
        IReadOnlyList<(ServerManagerItemViewModel Product, ServerBranchChannel Branch)> refreshedUpdates)
    {
        return refreshedUpdates
            .Select(refreshed =>
            {
                var confirmed = confirmedUpdates.FirstOrDefault(update => update.Product == refreshed.Product);
                return confirmed.Product is null ? refreshed : confirmed;
            })
            .ToArray();
    }

    private static (string Progress, string Past, string Failure) DescribeOperation(ServerOperation operation)
    {
        return operation switch
        {
            ServerOperation.Install => ("Installing", "was installed", "install"),
            ServerOperation.Repair => ("Repairing", "was repaired", "repair"),
            _ => ("Updating", "was updated", "update")
        };
    }

    private enum ServerOperation
    {
        Install,
        Update,
        Repair
    }

    /// <summary>Skipped means the user chose to keep local edits, which is not a failure.</summary>
    internal enum ServerOperationOutcome
    {
        Succeeded,
        Skipped,
        Failed
    }
}
