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
    private const string ServerManagementNeutralColor = "#C8C8C8";
    private const string ServerManagementErrorColor = "#FF8A80";

    /// <summary>Separates the distro core update from the shared components update in the console.</summary>
    private const string SharedComponentsMarker = "=====MARKER:BEGIN_SHARED_COMPONENTS=====";

    private ServerManagementService? _serverManagement;
    private string _serverManagementStatusText = "Checking installed servers...";
    private string _serverManagementStatusColor = ServerManagementNeutralColor;

    public ServerManagerItemViewModel HerikaManager { get; private set; } = null!;

    public ServerManagerItemViewModel StobeManager { get; private set; } = null!;

    public ServerManagerItemViewModel DialecticManager { get; private set; } = null!;

    /// <summary>The three products in rail order. Backs status refresh and the Update Mods sweep.</summary>
    public IReadOnlyList<ServerManagerItemViewModel> ServerManagers { get; private set; } = [];

    public string ServerManagementStatusText
    {
        get => _serverManagementStatusText;
        private set => SetProperty(ref _serverManagementStatusText, value);
    }

    public string ServerManagementStatusColor
    {
        get => _serverManagementStatusColor;
        private set => SetProperty(ref _serverManagementStatusColor, value);
    }

    /// <summary>
    /// The per-product update checkbox needs both the saved preference load and a real install; an
    /// unchecked-but-enabled box on a missing product would promise an update that cannot happen.
    /// </summary>
    public bool IsHerikaUpdateIncludeEnabled => IsUpdateIncludeReady && HerikaManager.CanUseInstalledFeatures;

    public bool IsStobeUpdateIncludeEnabled => IsUpdateIncludeReady && StobeManager.CanUseInstalledFeatures;

    public bool IsDialecticUpdateIncludeEnabled => IsUpdateIncludeReady && DialecticManager.CanUseInstalledFeatures;

    private void InitializeServerManagement()
    {
        _serverManagement = new ServerManagementService(_wsl);

        HerikaManager = CreateServerManagerItem(ServerProduct.Herika, "CHIM");
        StobeManager = CreateServerManagerItem(ServerProduct.Stobe, "STOBE");
        DialecticManager = CreateServerManagerItem(ServerProduct.Dialectic, "DIALECTIC");
        ServerManagers = [HerikaManager, StobeManager, DialecticManager];
    }

    private ServerManagerItemViewModel CreateServerManagerItem(ServerProduct product, string gameKey)
    {
        return new ServerManagerItemViewModel(
            product,
            gameKey,
            InstallServerAsync,
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
    /// Re-reads <c>status all --json</c> and pushes it into the three items. A failed probe keeps the
    /// last known state and shows why, rather than claiming everything is missing.
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

                SetServerManagementStatus($"Installed servers: {result.Error}", ServerManagementErrorColor);
                RaiseServerManagerDependentStates();
                return;
            }

            foreach (var manager in ServerManagers)
            {
                manager.ApplyStatus(result.Snapshot!.Find(manager.Product));
            }

            SetServerManagementStatus(BuildServerManagementSummary(ServerManagers), ServerManagementNeutralColor);
            RaiseServerManagerDependentStates();
        });
    }

    internal static string BuildServerManagementSummary(IReadOnlyList<ServerManagerItemViewModel> managers)
    {
        var installed = managers.Count(manager => manager.IsInstalled);
        var needsRepair = managers.Count(manager => manager.NeedsRepair);
        var summary = $"{installed} of {managers.Count} servers installed.";
        return needsRepair > 0 ? $"{summary} {needsRepair} need repair." : summary;
    }

    private void SetServerManagementStatus(string text, string color)
    {
        ServerManagementStatusText = text;
        ServerManagementStatusColor = color;
    }

    private void RaiseServerManagerDependentStates()
    {
        OnPropertyChanged(nameof(IsHerikaUpdateIncludeEnabled));
        OnPropertyChanged(nameof(IsStobeUpdateIncludeEnabled));
        OnPropertyChanged(nameof(IsDialecticUpdateIncludeEnabled));
        OpenChimCommand.RaiseCanExecuteChanged();
        OpenStobeCommand.RaiseCanExecuteChanged();
        OpenDialecticCommand.RaiseCanExecuteChanged();
        OpenHerikaRollbackCommand.RaiseCanExecuteChanged();
        OpenStobeRollbackCommand.RaiseCanExecuteChanged();
        OpenDialecticRollbackCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Feeds the existing version status into the matching product's status line.</summary>
    private void ApplyVersionStatusToManager(ServerProduct product, string text, string color)
    {
        var manager = ServerManagers.FirstOrDefault(item => item.Product == product);
        manager?.ApplyVersionStatus(text, color);
    }

    private async Task InstallServerAsync(ServerManagerItemViewModel item)
    {
        await RunServerOperationAsync(
                item,
                ServerOperation.Install,
                item.SelectedBranchChannel)
            .ConfigureAwait(true);
    }

    private async Task RepairServerAsync(ServerManagerItemViewModel item)
    {
        await RunServerOperationAsync(
                item,
                ServerOperation.Repair,
                item.SelectedBranchChannel)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Runs install or repair for one product, streaming manager output to the console and always
    /// refreshing status afterwards so a partial failure cannot leave a stale "Installed" label.
    /// </summary>
    private async Task<bool> RunServerOperationAsync(
        ServerManagerItemViewModel item,
        ServerOperation operation,
        ServerBranchChannel branch,
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
                _ => await service
                    .UpdateAsync(item.Product, branch, line => AppendLog(line), cancellationToken)
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
        if (service is null)
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
            var window = new ServerUninstallWindow(
                item.Product,
                item.DisplayName,
                item.Root,
                item.Database,
                item.DatabasePresent)
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

    // --- Update Mods -----------------------------------------------------------------------

    /// <summary>
    /// A product is updated only when it is really installed and the user left its update checkbox
    /// on. Update must never install a missing product, so a not-installed or needs-repair state is
    /// always excluded regardless of the checkbox.
    /// </summary>
    internal static bool ShouldUpdateProduct(ServerInstallState state, bool includeInUpdates)
    {
        return includeInUpdates && state == ServerInstallState.Installed;
    }

    /// <summary>
    /// The shared distro/components update always skips all three application servers: the server
    /// manager owns their repositories now, so update_gws only refreshes shared services.
    /// </summary>
    internal static string BuildSharedComponentsUpdateCommand()
    {
        return "/usr/local/bin/update_gws --skip-herika --skip-stobe --skip-dialectic";
    }

    private IReadOnlyList<ServerManagerItemViewModel> GetProductsToUpdate()
    {
        var include = new Dictionary<ServerProduct, bool>
        {
            [ServerProduct.Herika] = IncludeHerikaServerUpdate,
            [ServerProduct.Stobe] = IncludeStobeServerUpdate,
            [ServerProduct.Dialectic] = IncludeDialecticServerUpdate
        };

        return ServerManagers
            .Where(manager => ShouldUpdateProduct(manager.State, include[manager.Product]))
            .ToArray();
    }

    /// <summary>
    /// Runs the manager update for each selected product in turn. One failure does not abort the
    /// rest, so a healthy product still gets its update and the shared distro update still runs.
    /// </summary>
    private async Task<bool> UpdateInstalledServersAsync(IReadOnlyList<ServerManagerItemViewModel> products)
    {
        var allSucceeded = true;
        foreach (var product in products)
        {
            var succeeded = await RunServerOperationAsync(
                    product,
                    ServerOperation.Update,
                    product.SelectedBranchChannel)
                .ConfigureAwait(true);
            allSucceeded &= succeeded;
        }

        return allSucceeded;
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
