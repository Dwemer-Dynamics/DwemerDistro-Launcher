using System.Collections.ObjectModel;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// One optional application server as the Mods page sees it. Three of these live for the lifetime
/// of the window - they hold only scalars plus the two-item branch list, so keeping them resident
/// costs nothing and needs no extra views or subscriptions.
///
/// The Mods page shows one of three mutually exclusive control sets, driven by
/// <see cref="ShowNotInstalledActions"/>, <see cref="ShowInstalledActions"/> and
/// <see cref="ShowRepairActions"/>. Status text is one fixed-height line whose colour carries the
/// state, so a busy or failed operation never changes a button's size.
/// </summary>
public sealed class ServerManagerItemViewModel : ObservableObject
{
    internal const string NotInstalledColor = "#C8C8C8";
    internal const string CheckingColor = "White";
    internal const string NeedsRepairColor = "#FF9F40";
    internal const string BusyColor = "#F4D8A6";
    internal const string ErrorColor = "#FF8A80";

    private readonly Func<ServerManagerItemViewModel, Task> _install;
    private readonly Func<ServerManagerItemViewModel, Task> _update;
    private readonly Func<ServerManagerItemViewModel, Task> _repair;
    private readonly Func<ServerManagerItemViewModel, Task> _uninstall;

    private ServerInstallState _state = ServerInstallState.Unknown;
    private ServerRepositoryState _repositoryState = ServerRepositoryState.Unknown;
    private bool? _databasePresent;
    private string? _root;
    private string? _database;
    private string? _branch;
    private string? _version;
    private int? _port;
    private string _versionStatusText = "Checking...";
    private string _versionStatusColor = CheckingColor;
    private string _selectedBranch = "Main";
    private bool _isBusy;
    private bool _isConflictingOperationRunning;
    private string? _busyText;
    private string? _errorText;

    public ServerManagerItemViewModel(
        ServerProduct product,
        string gameKey,
        Func<ServerManagerItemViewModel, Task> install,
        Func<ServerManagerItemViewModel, Task> update,
        Func<ServerManagerItemViewModel, Task> repair,
        Func<ServerManagerItemViewModel, Task> uninstall)
    {
        Product = product;
        GameKey = gameKey;
        DisplayName = ServerManagementService.GetDisplayName(product);
        PurgeToken = ServerManagementService.GetPurgeToken(product);
        UpdateActionName = BuildUpdateActionName(product);
        Branches = new ObservableCollection<string>(new[] { "Main", "Dev" });
        _install = install;
        _update = update;
        _repair = repair;
        _uninstall = uninstall;

        InstallCommand = new AsyncRelayCommand(() => _install(this), () => CanInstall);
        UpdateCommand = new AsyncRelayCommand(() => _update(this), () => CanUpdate);
        RepairCommand = new AsyncRelayCommand(() => _repair(this), () => CanRepair);
        UninstallCommand = new AsyncRelayCommand(() => _uninstall(this), () => CanUninstall);
    }

    public ServerProduct Product { get; }

    /// <summary>Rail key ("CHIM", "STOBE", "DIALECTIC") this product backs.</summary>
    public string GameKey { get; }

    public string DisplayName { get; }

    public string PurgeToken { get; }

    /// <summary>
    /// Label for the single-product update action ("Update CHIM"). It names the rail product the
    /// mod page is showing rather than the server binary, so it never reads as a second copy of the
    /// top-level Update Mods sweep.
    /// </summary>
    public string UpdateActionName { get; }

    public ObservableCollection<string> Branches { get; }

    public AsyncRelayCommand InstallCommand { get; }

    /// <summary>Updates this product alone, on its selected branch. Shared components stay untouched.</summary>
    public AsyncRelayCommand UpdateCommand { get; }

    public AsyncRelayCommand RepairCommand { get; }

    public AsyncRelayCommand UninstallCommand { get; }

    public ServerInstallState State => _state;

    public ServerRepositoryState RepositoryState => _repositoryState;

    public string? Root => _root;

    public string? Database => _database;

    public int? Port => _port;

    public bool? DatabasePresent => _databasePresent;

    public string? InstalledBranch => _branch;

    public string? InstalledVersion => _version;

    public bool IsStatusKnown => _state != ServerInstallState.Unknown;

    public bool IsInstalled => _state == ServerInstallState.Installed;

    public bool IsNotInstalled => _state == ServerInstallState.NotInstalled;

    public bool NeedsRepair => _state == ServerInstallState.NeedsRepair;

    /// <summary>Install branch picker plus the Install Server action.</summary>
    public bool ShowNotInstalledActions => _state == ServerInstallState.NotInstalled;

    /// <summary>Branch picker, webpage, rollback and Uninstall Server.</summary>
    public bool ShowInstalledActions => _state == ServerInstallState.Installed;

    /// <summary>Repair Installation plus Uninstall Server.</summary>
    public bool ShowRepairActions => _state == ServerInstallState.NeedsRepair;

    public bool CanInstall => _state == ServerInstallState.NotInstalled && !_isBusy;

    public bool CanRepair => _state == ServerInstallState.NeedsRepair && !_isBusy;

    /// <summary>
    /// The single-product update needs a confirmed install - the manager never installs a missing or
    /// broken product - and must stay out of reach while the Update Mods sweep or another product's
    /// operation is already driving the manager.
    /// </summary>
    public bool CanUpdate =>
        _state == ServerInstallState.Installed && !_isBusy && !_isConflictingOperationRunning;

    /// <summary>A product with anything on disk can be purged; a clean absence cannot.</summary>
    public bool CanUninstall =>
        (_state == ServerInstallState.Installed || _state == ServerInstallState.NeedsRepair) && !_isBusy;

    /// <summary>Webpage, rollback and the update checkbox are only meaningful for a real install.</summary>
    public bool CanUseInstalledFeatures => _state == ServerInstallState.Installed && !_isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseDerivedState();
            }
        }
    }

    /// <summary>
    /// Set by the window while the Update Mods sweep or another product's install, update or repair
    /// is in flight. It gates only the single-product update action; install, repair and uninstall
    /// keep their existing lifecycle rules.
    /// </summary>
    public bool IsConflictingOperationRunning
    {
        get => _isConflictingOperationRunning;
        set
        {
            if (SetProperty(ref _isConflictingOperationRunning, value))
            {
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(UpdateActionHelpText));
                UpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedBranch
    {
        get => _selectedBranch;
        set
        {
            if (SetProperty(ref _selectedBranch, value))
            {
                OnPropertyChanged(nameof(UpdateActionHelpText));
            }
        }
    }

    public ServerBranchChannel SelectedBranchChannel => ServerManagementService.ParseBranchChannel(_selectedBranch);

    /// <summary>
    /// The single status line. Busy and error states take priority, then the manager state, then the
    /// existing green/yellow version semantics for an installed product.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (_isBusy && !string.IsNullOrWhiteSpace(_busyText))
            {
                return _busyText!;
            }

            if (!string.IsNullOrWhiteSpace(_errorText))
            {
                return _errorText!;
            }

            return _state switch
            {
                ServerInstallState.NotInstalled => "Not installed",
                ServerInstallState.NeedsRepair => "Needs repair",
                ServerInstallState.Installed => _versionStatusText,
                _ => _versionStatusText
            };
        }
    }

    public string StatusColor
    {
        get
        {
            if (_isBusy && !string.IsNullOrWhiteSpace(_busyText))
            {
                return BusyColor;
            }

            if (!string.IsNullOrWhiteSpace(_errorText))
            {
                return ErrorColor;
            }

            return _state switch
            {
                ServerInstallState.NotInstalled => NotInstalledColor,
                ServerInstallState.NeedsRepair => NeedsRepairColor,
                ServerInstallState.Installed => _versionStatusColor,
                _ => CheckingColor
            };
        }
    }

    /// <summary>Root, database and port for the uninstall dialog and the status tooltip.</summary>
    public string LocationSummary
    {
        get
        {
            var parts = new List<string>
            {
                $"Files: {_root ?? "unknown"}",
                $"Database: {_database ?? "unknown"}" + (_databasePresent == false ? " (missing)" : string.Empty)
            };

            if (_port is not null)
            {
                parts.Add($"Port: {_port}");
            }

            return string.Join(" | ", parts);
        }
    }

    public string AccessibleStatusHelpText =>
        $"{DisplayName}: {StatusText}. {LocationSummary}";

    /// <summary>
    /// Says what the single-product update will do and, when it is unavailable, why - so a screen
    /// reader landing on the disabled button hears a reason instead of only "dimmed".
    /// </summary>
    public string UpdateActionHelpText
    {
        get
        {
            if (_isBusy)
            {
                return $"{UpdateActionName} is unavailable while {DisplayName} is busy.";
            }

            if (_isConflictingOperationRunning)
            {
                return $"{UpdateActionName} is unavailable while another server update is running. " +
                       "It stays available once that finishes.";
            }

            return $"Update only {DisplayName}, on the selected {SelectedBranch} branch. " +
                   "The shared distro components, the other servers, and the Updates checkbox are left alone.";
        }
    }

    /// <summary>Rail-facing label for the per-product update action.</summary>
    internal static string BuildUpdateActionName(ServerProduct product)
    {
        return product switch
        {
            ServerProduct.Herika => "Update CHIM",
            ServerProduct.Stobe => "Update STOBE",
            ServerProduct.Dialectic => "Update Dialectic",
            _ => throw new ArgumentOutOfRangeException(nameof(product), product, "Unknown server product.")
        };
    }

    /// <summary>Applies a fresh status entry. A null entry means the probe did not list the product.</summary>
    public void ApplyStatus(ServerStatus? status)
    {
        _state = status?.State ?? ServerInstallState.Unknown;
        _repositoryState = status?.RepositoryState ?? ServerRepositoryState.Unknown;
        _databasePresent = status?.DatabasePresent;
        _root = status?.Root;
        _database = status?.Database;
        _branch = status?.Branch;
        _version = status?.Version;
        _port = status?.Port;
        _errorText = null;

        if (status?.Branch is not null)
        {
            var channel = MapBranchToChannel(status.Branch, status.ProductionBranch, status.DevelopmentBranch);
            if (channel is not null)
            {
                SelectedBranch = ServerManagementService.ToBranchChoice(channel.Value);
            }
        }

        RaiseDerivedState();
    }

    /// <summary>Records a status-probe failure without discarding what the UI already showed.</summary>
    public void ApplyStatusError(string error)
    {
        _errorText = error;
        RaiseDerivedState();
    }

    /// <summary>Feeds the existing per-product version check into the installed status line.</summary>
    public void ApplyVersionStatus(string text, string color)
    {
        _versionStatusText = text;
        _versionStatusColor = color;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(AccessibleStatusHelpText));
    }

    public void BeginOperation(string busyText)
    {
        _busyText = busyText;
        _errorText = null;
        IsBusy = true;
    }

    public void UpdateOperationText(string busyText)
    {
        _busyText = busyText;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AccessibleStatusHelpText));
    }

    public void EndOperation(string? error = null)
    {
        _busyText = null;
        _errorText = error;
        IsBusy = false;
    }

    /// <summary>
    /// Chooses the visible branch entry from the checked-out branch, preferring the exact
    /// production/development names the manager reported for this product.
    /// </summary>
    internal static ServerBranchChannel? MapBranchToChannel(
        string? branch,
        string? productionBranch,
        string? developmentBranch)
    {
        var normalized = branch?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(productionBranch) &&
            string.Equals(normalized, productionBranch.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return ServerBranchChannel.Main;
        }

        if (!string.IsNullOrWhiteSpace(developmentBranch) &&
            string.Equals(normalized, developmentBranch.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return ServerBranchChannel.Dev;
        }

        return normalized.ToLowerInvariant() switch
        {
            "main" or "master" or "aiagent" or "stobe" or "dialectic" => ServerBranchChannel.Main,
            "dev" or "unstable" => ServerBranchChannel.Dev,
            _ => null
        };
    }

    private void RaiseDerivedState()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(RepositoryState));
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(Database));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(DatabasePresent));
        OnPropertyChanged(nameof(InstalledBranch));
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(IsStatusKnown));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsNotInstalled));
        OnPropertyChanged(nameof(NeedsRepair));
        OnPropertyChanged(nameof(ShowNotInstalledActions));
        OnPropertyChanged(nameof(ShowInstalledActions));
        OnPropertyChanged(nameof(ShowRepairActions));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRepair));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanUseInstalledFeatures));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(LocationSummary));
        OnPropertyChanged(nameof(AccessibleStatusHelpText));
        OnPropertyChanged(nameof(UpdateActionHelpText));
        InstallCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        RepairCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
    }
}
