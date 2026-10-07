using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private CustomModInfo? _selectedCustomMod;
    private string _customModsStatusText = string.Empty;
    private ImageSource? _selectedCustomModHeroImage;

    // Hero artwork per repository URL, fetched at most once per session. A null entry means the
    // fetch is pending or failed, so the generic placeholder stays.
    private readonly Dictionary<string, ImageSource?> _customModHeroImages = new(StringComparer.Ordinal);

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
            OnPropertyChanged(nameof(SelectedCustomModRepositoryActionName));
            ShowSelectedCustomModHero();
            RaiseCustomModCommandStates();
        }
    }

    public bool HasSelectedCustomMod => _selectedCustomMod is not null;

    /// <summary>Public repository artwork for the selected mod, or null for the generic placeholder.</summary>
    public ImageSource? SelectedCustomModHeroImage
    {
        get => _selectedCustomModHeroImage;
        private set
        {
            if (SetProperty(ref _selectedCustomModHeroImage, value))
            {
                OnPropertyChanged(nameof(HasSelectedCustomModHeroImage));
            }
        }
    }

    public bool HasSelectedCustomModHeroImage => _selectedCustomModHeroImage is not null;

    public string SelectedCustomModRepositoryActionName =>
        CustomModService.TryGetGitHubArtworkUrls(_selectedCustomMod?.Repository, out _) ? "GitHub Page" : "Repository Page";

    public bool HasNoCustomMods => CustomMods.Count == 0;

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
    /// distro, so the window does not close under it. A status refresh alone does not set this.
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

    public RelayCommand OpenCustomModRepositoryCommand { get; private set; } = null!;

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
        OpenCustomModRepositoryCommand = new RelayCommand(
            OpenCustomModRepository, () => CustomModService.ValidateRepositoryUrl(_selectedCustomMod?.Repository) is null);
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
        OpenCustomModRepositoryCommand?.RaiseCanExecuteChanged();
        UpdateCustomModCommand?.RaiseCanExecuteChanged();
        BackupCustomModCommand?.RaiseCanExecuteChanged();
        UnregisterCustomModCommand?.RaiseCanExecuteChanged();
    }

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

        IsCustomModBusy = true;
        CustomModsStatusText = checkHealth ? "Checking status…" : "Loading custom mods…";
        try
        {
            var result = await _customModService.GetStatusAsync(checkHealth).ConfigureAwait(true);
            if (result.Mods is null)
            {
                // Keep the previous list rather than clearing it on a failed probe.
                CustomModsStatusText = result.Error ?? "Could not read custom mods.";
                return;
            }

            ApplyCustomMods(result.Mods);
            CustomModsStatusText = result.Mods.Count == 0 ? "No custom mods yet." : string.Empty;
        }
        catch (Exception ex)
        {
            CustomModsStatusText = "Could not read custom mods: " + ex.Message;
        }
        finally
        {
            IsCustomModBusy = false;
        }
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

    private void OpenCustomModRepository()
    {
        var repository = _selectedCustomMod?.Repository;
        if (CustomModService.ValidateRepositoryUrl(repository) is not null)
        {
            return;
        }

        _processRunner.OpenExternalUrl(repository!);
    }

    private void ShowSelectedCustomModHero()
    {
        var repository = _selectedCustomMod?.Repository;
        if (repository is null || !CustomModService.TryGetGitHubArtworkUrls(repository, out var artworkUrls))
        {
            SelectedCustomModHeroImage = null;
            return;
        }

        if (_customModHeroImages.TryGetValue(repository, out var cached))
        {
            SelectedCustomModHeroImage = cached;
            return;
        }

        _customModHeroImages[repository] = null;
        SelectedCustomModHeroImage = null;
        _ = LoadCustomModHeroAsync(repository, artworkUrls);
    }

    /// <summary>
    /// Anonymous GET of public GitHub artwork (social preview, then owner avatar). No credentials
    /// are attached; any failure leaves the generic placeholder.
    /// </summary>
    private async Task LoadCustomModHeroAsync(string repository, IReadOnlyList<string> artworkUrls)
    {
        const int maxBytes = 4 * 1024 * 1024;
        foreach (var url in artworkUrls)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await _httpClient
                    .GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(true);
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!response.IsSuccessStatusCode || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                    response.Content.Headers.ContentLength > maxBytes)
                {
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(true);
                if (bytes.Length == 0 || bytes.Length > maxBytes)
                {
                    continue;
                }

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();

                _customModHeroImages[repository] = image;
                if (_selectedCustomMod?.Repository == repository)
                {
                    SelectedCustomModHeroImage = image;
                }

                return;
            }
            catch (Exception)
            {
                // Offline, blocked, timed out, or not an image: try the next source.
            }
        }
    }

    private async Task AddCustomModAsync()
    {
        IsCustomModBusy = true;
        _isCustomModMutationInProgress = true;
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
            _isCustomModMutationInProgress = false;
            IsCustomModBusy = false;
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

        IsCustomModBusy = true;
        _isCustomModMutationInProgress = true;
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
            _isCustomModMutationInProgress = false;
            IsCustomModBusy = false;
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
