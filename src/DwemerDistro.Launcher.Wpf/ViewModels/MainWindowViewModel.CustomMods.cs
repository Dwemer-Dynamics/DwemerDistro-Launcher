using System.Collections.ObjectModel;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The Mods page's Custom mods view. The list and every operation come from the distro's
/// <see cref="CustomModService"/>; the launcher keeps no registry of its own. The Official view and
/// its behaviour are untouched by anything here.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private CustomModService _customModService = null!;
    private bool _isCustomModsView;
    private bool _isCustomModBusy;
    private bool _isCustomModMutationInProgress;
    private bool _isCustomModListLoaded;
    private CustomModInfo? _selectedCustomMod;
    private string _customModsStatusText = string.Empty;

    public ObservableCollection<CustomModInfo> CustomMods { get; } = [];

    public bool IsCustomModsView
    {
        get => _isCustomModsView;
        set
        {
            if (!SetProperty(ref _isCustomModsView, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsOfficialModsView));
            if (value)
            {
                _ = RefreshCustomModsAsync(checkHealth: false);
            }
        }
    }

    /// <summary>Second radio button of the Official/Custom switch.</summary>
    public bool IsOfficialModsView
    {
        get => !_isCustomModsView;
        set
        {
            if (value)
            {
                IsCustomModsView = false;
            }
        }
    }

    public CustomModInfo? SelectedCustomMod
    {
        get => _selectedCustomMod;
        set
        {
            if (!SetProperty(ref _selectedCustomMod, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedCustomMod));
            OnPropertyChanged(nameof(SelectedCustomModStateText));
            RaiseCustomModCommandStates();
        }
    }

    public bool HasSelectedCustomMod => _selectedCustomMod is not null;

    /// <summary>True only for a list the distro reported as empty, never for an unread or failed one.</summary>
    public bool HasNoCustomMods => IsConfirmedEmptyCustomModList(_isCustomModListLoaded, CustomMods.Count);

    internal static bool IsConfirmedEmptyCustomModList(bool listLoaded, int count)
    {
        return listLoaded && count == 0;
    }

    public bool IsCustomModBusy
    {
        get => _isCustomModBusy;
        private set
        {
            if (!SetProperty(ref _isCustomModBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsComponentInteractionEnabled));
            RaiseUpdateCommandStates();
            RaiseCustomModCommandStates();
        }
    }

    /// <summary>
    /// True while a custom install, restore, update, backup, or unregister may be running in the
    /// distro, so the window does not close under it and the distro update, stop, and maintenance
    /// gates stay shut. A status refresh alone does not set this.
    /// </summary>
    public bool IsCustomModMutationInProgress => _isCustomModMutationInProgress;

    public string CustomModsStatusText
    {
        get => _customModsStatusText;
        private set => SetProperty(ref _customModsStatusText, value);
    }

    public string SelectedCustomModStateText => _selectedCustomMod is null
        ? string.Empty
        : DescribeCustomModState(_selectedCustomMod);

    public AsyncRelayCommand AddCustomModCommand { get; private set; } = null!;

    public AsyncRelayCommand CheckCustomModHealthCommand { get; private set; } = null!;

    public RelayCommand OpenCustomModDashboardCommand { get; private set; } = null!;

    public AsyncRelayCommand UpdateCustomModCommand { get; private set; } = null!;

    public AsyncRelayCommand BackupCustomModCommand { get; private set; } = null!;

    public AsyncRelayCommand UnregisterCustomModCommand { get; private set; } = null!;

    private void InitializeCustomMods()
    {
        _customModService = new CustomModService(_wsl);
        CustomMods.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoCustomMods));
        AddCustomModCommand = new AsyncRelayCommand(AddCustomModAsync, CanStartCustomModOperation);
        CheckCustomModHealthCommand = new AsyncRelayCommand(
            () => RunCustomModStatusCheckAsync(), () => CanStartCustomModOperation() && HasSelectedCustomMod);
        OpenCustomModDashboardCommand = new RelayCommand(
            OpenCustomModDashboard, () => _selectedCustomMod?.DashboardUrl is not null && _selectedCustomMod.State == CustomModState.Ready);
        UpdateCustomModCommand = new AsyncRelayCommand(
            () => RunSelectedCustomModOperationAsync("Update", _customModService.UpdateAsync),
            () => CanStartCustomModOperation() && HasSelectedCustomMod);
        BackupCustomModCommand = new AsyncRelayCommand(
            () => RunSelectedCustomModOperationAsync("Backup", _customModService.BackupAsync),
            () => CanStartCustomModOperation() && HasSelectedCustomMod);
        UnregisterCustomModCommand = new AsyncRelayCommand(
            UnregisterSelectedCustomModAsync, () => CanStartCustomModOperation() && HasSelectedCustomMod);
    }

    private bool CanStartCustomModOperation()
    {
        return !_isCustomModBusy && IsComponentInteractionEnabled && CanAccessDistro();
    }

    private void RaiseCustomModCommandStates()
    {
        AddCustomModCommand?.RaiseCanExecuteChanged();
        CheckCustomModHealthCommand?.RaiseCanExecuteChanged();
        OpenCustomModDashboardCommand?.RaiseCanExecuteChanged();
        UpdateCustomModCommand?.RaiseCanExecuteChanged();
        BackupCustomModCommand?.RaiseCanExecuteChanged();
        UnregisterCustomModCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Registers a root mutation as distro activity so Compact, Export, Import, and Fix WSL DNS
    /// refuse to start, and sets the flag the update, stop, and launcher-update gates read.
    /// </summary>
    private bool TryBeginCustomModMutation()
    {
        _isCustomModMutationInProgress = true;
        if (TryBeginPassiveDistroActivity())
        {
            return true;
        }

        _isCustomModMutationInProgress = false;
        CustomModsStatusText = CustomModsMaintenanceMessage;
        return false;
    }

    private void EndCustomModMutation()
    {
        _isCustomModMutationInProgress = false;
        EndPassiveDistroActivity();
    }

    internal const string CustomModsMaintenanceMessage =
        "Custom mods are unavailable while distro maintenance is running. Try again when it finishes.";

    internal static string DescribeCustomModState(CustomModInfo mod)
    {
        var state = mod.State switch
        {
            CustomModState.Ready => "Installed",
            CustomModState.Busy => "Working…",
            _ => "Needs attention"
        };
        var health = mod.Health switch
        {
            "ok" => " · Healthy",
            "failed" => " · " + (mod.HealthMessage ?? "Not responding"),
            _ => string.Empty
        };
        var message = mod.State == CustomModState.Ready || string.IsNullOrEmpty(mod.Message)
            ? string.Empty
            : Environment.NewLine + mod.Message;
        return state + health + message;
    }

    private async Task RefreshCustomModsAsync(bool checkHealth)
    {
        if (_isCustomModBusy)
        {
            return;
        }

        // A status read starts the distro, so it must not run under Compact, Export, or Import.
        if (!TryBeginPassiveDistroActivity())
        {
            CustomModsStatusText = CustomModsMaintenanceMessage;
            return;
        }

        IsCustomModBusy = true;
        CustomModsStatusText = checkHealth ? "Checking status…" : "Loading custom mods…";
        try
        {
            var result = await _customModService.GetStatusAsync(checkHealth).ConfigureAwait(true);
            if (result.Mods is null)
            {
                // Keep the previous list rather than clearing it on a failed probe.
                SetCustomModListLoaded(false);
                CustomModsStatusText = result.Error ?? "Could not read custom mods.";
                return;
            }

            ApplyCustomMods(result.Mods);
            SetCustomModListLoaded(true);
            CustomModsStatusText = result.Mods.Count == 0 ? "No custom mods yet." : string.Empty;
        }
        catch (Exception ex)
        {
            SetCustomModListLoaded(false);
            CustomModsStatusText = "Could not read custom mods: " + ex.Message;
        }
        finally
        {
            IsCustomModBusy = false;
            EndPassiveDistroActivity();
        }
    }

    private void SetCustomModListLoaded(bool loaded)
    {
        if (_isCustomModListLoaded == loaded)
        {
            return;
        }

        _isCustomModListLoaded = loaded;
        OnPropertyChanged(nameof(HasNoCustomMods));
    }

    private void ApplyCustomMods(IReadOnlyList<CustomModInfo> mods)
    {
        var selectedId = _selectedCustomMod?.Id;
        CustomMods.Clear();
        foreach (var mod in mods)
        {
            CustomMods.Add(mod);
        }

        SelectedCustomMod = CustomMods.FirstOrDefault(mod => mod.Id == selectedId) ?? CustomMods.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCustomModStateText));
    }

    private Task RunCustomModStatusCheckAsync()
    {
        return RefreshCustomModsAsync(checkHealth: true);
    }

    private void OpenCustomModDashboard()
    {
        var mod = _selectedCustomMod;
        if (mod?.DashboardUrl is null || !CustomModService.IsSafeModUrl(mod.DashboardUrl, mod.Id))
        {
            return;
        }

        _processRunner.OpenExternalUrl(mod.DashboardUrl);
    }

    private async Task AddCustomModAsync()
    {
        if (!TryBeginCustomModMutation())
        {
            return;
        }

        IsCustomModBusy = true;
        bool installed;
        try
        {
            var dialog = new AddCustomModWindow(_customModService, line => AppendLog(line + Environment.NewLine))
            {
                Owner = Application.Current?.MainWindow
            };
            installed = dialog.ShowDialog() == true || dialog.InstallAttempted;
        }
        finally
        {
            IsCustomModBusy = false;
            EndCustomModMutation();
        }

        if (installed)
        {
            await RefreshCustomModsAsync(checkHealth: false).ConfigureAwait(true);
        }
    }

    private async Task RunSelectedCustomModOperationAsync(
        string label,
        Func<string, Action<string>?, CancellationToken, Task<Models.CommandResult>> operation)
    {
        var mod = _selectedCustomMod;
        if (mod is null)
        {
            return;
        }

        if (!TryBeginCustomModMutation())
        {
            return;
        }

        IsCustomModBusy = true;
        CustomModsStatusText = $"{label} {mod.Name}…";
        AppendLog($"{label} custom mod {mod.Name}.{Environment.NewLine}");
        try
        {
            var result = await operation(mod.Id, line => AppendLog(line + Environment.NewLine), CancellationToken.None)
                .ConfigureAwait(true);
            CustomModsStatusText = result.Succeeded
                ? $"{label} finished."
                : CustomModService.DescribeFailure(result);
        }
        catch (Exception ex)
        {
            CustomModsStatusText = $"{label} failed: {ex.Message}";
        }
        finally
        {
            IsCustomModBusy = false;
            EndCustomModMutation();
        }

        var message = CustomModsStatusText;
        await RefreshCustomModsAsync(checkHealth: false).ConfigureAwait(true);
        CustomModsStatusText = message;
    }

    private async Task UnregisterSelectedCustomModAsync()
    {
        var mod = _selectedCustomMod;
        if (mod is null)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"Remove {mod.Name} from the Custom mods list?\n\n" +
            "Its files and database are kept in the distro. Nothing is deleted.",
            "Unregister custom mod",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        await RunSelectedCustomModOperationAsync("Unregister", _customModService.UnregisterAsync).ConfigureAwait(true);
    }
}
