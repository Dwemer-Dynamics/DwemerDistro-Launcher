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

    /// <summary>The three products in rail order. Backs status refresh and every mod update.</summary>
    public IReadOnlyList<ServerManagerItemViewModel> ServerManagers { get; private set; } = [];

    private void InitializeServerManagement()
    {
        _serverManagement = new ServerManagementService(_wsl);

        HerikaManager = CreateServerManagerItem(ServerProduct.Herika, "CHIM");
        StobeManager = CreateServerManagerItem(ServerProduct.Stobe, "STOBE");
        DialecticManager = CreateServerManagerItem(ServerProduct.Dialectic, "DIALECTIC");
        ServerManagers = [HerikaManager, StobeManager, DialecticManager];

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
            OpenHerikaRollbackCommand?.RaiseCanExecuteChanged();
            OpenStobeRollbackCommand?.RaiseCanExecuteChanged();
            OpenDialecticRollbackCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Keeps server actions out of the way while any server, component or system operation runs.
    /// </summary>
    internal void RefreshServerUpdateConflictState()
    {
        var busy = IsDistroUpdateInProgress ||
                   _isComponentsOperationInProgress ||
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

        var result = await service.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (!result.IsSuccess)
            {
                foreach (var manager in ServerManagers)
                {
                    manager.ApplyStatusError("Server status unavailable");
                }

                RaiseServerManagerDependentStates();
                return;
            }

            foreach (var manager in ServerManagers)
            {
                manager.ApplyStatus(result.Snapshot!.Find(manager.Product));
            }

            RaiseServerManagerDependentStates();
        });
    }

    private void RaiseServerManagerDependentStates()
    {
        OnPropertyChanged(nameof(IsComponentInteractionEnabled));
        RaiseUpdateCommandStates();
        RefreshServerUpdateConflictState();
        OpenChimCommand.RaiseCanExecuteChanged();
        OpenStobeCommand.RaiseCanExecuteChanged();
        OpenDialecticCommand.RaiseCanExecuteChanged();
        OpenHerikaRollbackCommand.RaiseCanExecuteChanged();
        OpenStobeRollbackCommand.RaiseCanExecuteChanged();
        OpenDialecticRollbackCommand.RaiseCanExecuteChanged();
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
    private async Task<bool> RunServerOperationAsync(
        ServerManagerItemViewModel item,
        ServerOperation operation,
        ServerBranchChannel branch,
        bool forceGitUpdates = false,
        CancellationToken cancellationToken = default)
    {
        var service = _serverManagement;
        if (service is null)
        {
            return false;
        }

        var (progressVerb, pastVerb, failureVerb) = DescribeOperation(operation);
        var branchChoice = ServerManagementService.ToBranchChoice(branch);

        RunOnUi(() => item.BeginOperation($"{progressVerb} ({branchChoice})..."));
        AppendLog(
            $"{Environment.NewLine}{progressVerb} {item.DisplayName} on the {branchChoice} branch...{Environment.NewLine}",
            "green");

        var succeeded = false;
        try
        {
            var result = operation switch
            {
                ServerOperation.Install => await service
                    .InstallAsync(item.Product, branch, line => AppendLog(line), cancellationToken)
                    .ConfigureAwait(false),
                ServerOperation.Repair => await service
                    .RepairAsync(item.Product, branch, line => AppendLog(line), cancellationToken)
                    .ConfigureAwait(false),
                // Only update can force: the setting exists so a dirty worktree stops blocking an
                // update, and install and repair have no confirmed edits to discard.
                _ => await service
                    .UpdateAsync(item.Product, branch, forceGitUpdates, line => AppendLog(line), cancellationToken)
                    .ConfigureAwait(false)
            };

            succeeded = result.Succeeded;
            AppendLog(
                succeeded
                    ? $"{item.DisplayName} {pastVerb} successfully.{Environment.NewLine}"
                    : $"{item.DisplayName} {failureVerb} failed: {GetCommandError(result)}{Environment.NewLine}",
                succeeded ? "green" : "red");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{item.DisplayName} {failureVerb} was canceled.{Environment.NewLine}", "yellow");
        }
        catch (Exception ex)
        {
            AppendLog($"{item.DisplayName} {failureVerb} failed: {ex.Message}{Environment.NewLine}", "red");
        }
        finally
        {
            RunOnUi(() => item.EndOperation(succeeded ? null : $"Last {failureVerb} failed"));
            await RefreshServerManagementSafeAsync().ConfigureAwait(false);
            QueueServerVersionRefresh(item.Product);
        }

        return succeeded;
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
        return "/usr/local/bin/update_gws --skip-herika --skip-stobe --skip-dialectic";
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
}
