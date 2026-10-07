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
    private string? _selectedCustomModBranch;
    private string _customModsStatusText = string.Empty;
    private ImageSource? _selectedCustomModHeroImage;

    // Hero artwork and sidebar icons, keyed by installed commit plus source URLs, so an Update that
    // changes an asset is fetched again. A null hero entry means the fetch is pending or failed, so the
    // generic placeholder stays; a mod without a loaded icon shows its name alone.
    private readonly Dictionary<string, ImageSource?> _customModHeroImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _customModIconImages = new(StringComparer.Ordinal);

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
            OnPropertyChanged(nameof(SelectedCustomModBranchOptions));
            OnPropertyChanged(nameof(SelectedCustomModBranchNotice));
            OnPropertyChanged(nameof(HasSelectedCustomModBranchNotice));
            // The picker always starts on the installed branch; a switch is only ever explicit.
            SelectedCustomModBranch = value?.Branch;
            ShowSelectedCustomModHero();
            RaiseCustomModCommandStates();
        }
    }

    public bool HasSelectedCustomMod => _selectedCustomMod is not null;

    /// <summary>Offered branches from the last check, plus the installed one if the author removed it.</summary>
    public IReadOnlyList<string> SelectedCustomModBranchOptions => GetCustomModBranchOptions(_selectedCustomMod);

    public string? SelectedCustomModBranch
    {
        get => _selectedCustomModBranch;
        set
        {
            if (SetProperty(ref _selectedCustomModBranch, value))
            {
                SwitchCustomModBranchCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedCustomModBranchNotice => DescribeCustomModBranches(_selectedCustomMod);

    public bool HasSelectedCustomModBranchNotice => SelectedCustomModBranchNotice.Length > 0;

    /// <summary>Installed banner or public repository artwork for the selected mod, or null for the generic placeholder.</summary>
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

    public AsyncRelayCommand CheckCustomModBranchesCommand { get; private set; } = null!;

    public AsyncRelayCommand SwitchCustomModBranchCommand { get; private set; } = null!;

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
        CheckCustomModBranchesCommand = new AsyncRelayCommand(
            CheckSelectedCustomModBranchesAsync, () => CanStartCustomModOperation() && HasSelectedCustomMod);
        SwitchCustomModBranchCommand = new AsyncRelayCommand(
            SwitchSelectedCustomModBranchAsync,
            () => CanStartCustomModOperation() && CanSwitchCustomModBranch(_selectedCustomMod, _selectedCustomModBranch));
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
        CheckCustomModBranchesCommand?.RaiseCanExecuteChanged();
        SwitchCustomModBranchCommand?.RaiseCanExecuteChanged();
    }

    internal static IReadOnlyList<string> GetCustomModBranchOptions(CustomModInfo? mod)
    {
        if (mod is null)
        {
            return [];
        }

        var options = mod.OfferedBranches.ToList();
        if (CustomModService.IsValidBranchName(mod.Branch) && !options.Contains(mod.Branch, StringComparer.Ordinal))
        {
            options.Insert(0, mod.Branch);
        }

        return options;
    }

    /// <summary>Only another branch the author offers right now; the installed branch is never "switched" to.</summary>
    internal static bool CanSwitchCustomModBranch(CustomModInfo? mod, string? target)
    {
        return mod is not null && CustomModService.IsValidBranchName(target) &&
               !string.Equals(target, mod.Branch, StringComparison.Ordinal) &&
               mod.OfferedBranches.Contains(target!, StringComparer.Ordinal);
    }

    internal static string DescribeCustomModBranches(CustomModInfo? mod)
    {
        if (mod is null)
        {
            return string.Empty;
        }

        if (!mod.IsBranchOffered)
        {
            return $"Branch {mod.Branch} is no longer offered by the author. Update keeps it; choose an offered branch and Switch to move.";
        }

        return mod.DefaultBranch.Length > 0 && mod.DefaultBranch != mod.Branch
            ? $"The author's default branch is {mod.DefaultBranch}."
            : string.Empty;
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

            ApplyCustomMods(await WithCustomModIconsAsync(result.Mods).ConfigureAwait(true));
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

    /// <summary>
    /// Hero sources in priority order: the installed banner on the fixed local route, then the
    /// public GitHub social preview and owner avatar. Empty means the generic placeholder.
    /// </summary>
    internal static List<string> GetCustomModHeroUrls(CustomModInfo? mod)
    {
        var urls = new List<string>();
        if (mod is null)
        {
            return urls;
        }

        if (CustomModService.IsSafeModAssetUrl(mod.BannerUrl, mod.Id))
        {
            urls.Add(mod.BannerUrl!);
        }

        if (CustomModService.TryGetGitHubArtworkUrls(mod.Repository, out var githubUrls))
        {
            urls.AddRange(githubUrls);
        }

        return urls;
    }

    private void ShowSelectedCustomModHero()
    {
        var mod = _selectedCustomMod;
        var artworkUrls = GetCustomModHeroUrls(mod);
        if (artworkUrls.Count == 0)
        {
            SelectedCustomModHeroImage = null;
            return;
        }

        var key = GetCustomModHeroCacheKey(mod!, artworkUrls);
        if (_customModHeroImages.TryGetValue(key, out var cached))
        {
            SelectedCustomModHeroImage = cached;
            return;
        }

        _customModHeroImages[key] = null;
        SelectedCustomModHeroImage = null;
        _ = LoadCustomModHeroAsync(mod!.Id, key, artworkUrls);
    }

    /// <summary>One hero image per installed revision and source list.</summary>
    private static string GetCustomModHeroCacheKey(CustomModInfo mod, IReadOnlyList<string> artworkUrls) =>
        mod.ShortCommit + "\n" + string.Join("\n", artworkUrls);

    private async Task LoadCustomModHeroAsync(string modId, string key, IReadOnlyList<string> artworkUrls)
    {
        foreach (var url in artworkUrls)
        {
            var image = await TryLoadCustomModImageAsync(url).ConfigureAwait(true);
            if (image is null)
            {
                continue;
            }

            _customModHeroImages[key] = image;
            // A slow load for an older revision of the same mod must not replace the newer one.
            var selected = _selectedCustomMod;
            if (selected?.Id == modId && GetCustomModHeroCacheKey(selected, GetCustomModHeroUrls(selected)) == key)
            {
                SelectedCustomModHeroImage = image;
            }

            return;
        }
    }

    /// <summary>Attaches each mod's installed icon, fetched once per installed revision; a failure keeps the name alone.</summary>
    private async Task<IReadOnlyList<CustomModInfo>> WithCustomModIconsAsync(IReadOnlyList<CustomModInfo> mods)
    {
        var pending = mods
            .Where(mod => CustomModService.IsSafeModAssetUrl(mod.IconUrl, mod.Id))
            .Select(mod => mod.ShortCommit + "\n" + mod.IconUrl)
            .Distinct(StringComparer.Ordinal)
            .Where(key => !_customModIconImages.ContainsKey(key))
            .ToList();
        var loaded = await Task.WhenAll(pending.Select(key => TryLoadCustomModImageAsync(key[(key.IndexOf('\n') + 1)..])))
            .ConfigureAwait(true);
        for (var i = 0; i < pending.Count; i++)
        {
            // Only successes are kept, so an icon missed while the web server was starting loads on the next refresh.
            if (loaded[i] is not null)
            {
                _customModIconImages[pending[i]] = loaded[i];
            }
        }

        return mods
            .Select(mod => _customModIconImages.TryGetValue(mod.ShortCommit + "\n" + mod.IconUrl, out var icon) && icon is not null
                ? mod with { IconImage = icon }
                : mod)
            .ToList();
    }

    /// <summary>
    /// Anonymous, bounded GET of one image (installed asset or public GitHub artwork). No
    /// credentials are attached; offline, blocked, timed out, oversized, or non-image returns null.
    /// </summary>
    private async Task<ImageSource?> TryLoadCustomModImageAsync(string url)
    {
        const int maxBytes = 4 * 1024 * 1024;
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
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(true);
            if (bytes.Length == 0 || bytes.Length > maxBytes)
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
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

    private Task CheckSelectedCustomModBranchesAsync()
    {
        return RunSelectedCustomModOperationAsync("Check branches", async (id, output, cancellationToken) =>
        {
            var result = await _customModService.CheckBranchesAsync(id, null, output, cancellationToken).ConfigureAwait(true);
            return result.Check is not null
                ? new Models.CommandResult(0, string.Empty, string.Empty)
                : new Models.CommandResult(1, string.Empty, "[FAIL] " + result.Error);
        });
    }

    /// <summary>
    /// Reviews the exact commit of the chosen branch, asks for confirmation, then switches to that
    /// commit only. The window's close guard stays on while the distro is working.
    /// </summary>
    private async Task SwitchSelectedCustomModBranchAsync()
    {
        var mod = _selectedCustomMod;
        var target = _selectedCustomModBranch;
        if (mod is null || !CanSwitchCustomModBranch(mod, target))
        {
            return;
        }

        IsCustomModBusy = true;
        _isCustomModMutationInProgress = true;
        CustomModsStatusText = $"Checking branch {target}…";
        CustomModBranchCheckResult review;
        try
        {
            review = await _customModService.CheckBranchesAsync(mod.Id, target, line => AppendLog(line + Environment.NewLine))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            review = new CustomModBranchCheckResult(null, "Branch check failed: " + ex.Message);
        }
        finally
        {
            _isCustomModMutationInProgress = false;
            IsCustomModBusy = false;
        }

        var check = review.Check;
        if (check is null || check.Branch != target)
        {
            var error = review.Error ?? "The branch check returned an incomplete result.";
            await RefreshCustomModsAsync(checkHealth: false).ConfigureAwait(true);
            CustomModsStatusText = error;
            return;
        }

        var confirmed = MessageBox.Show(
            $"Switch {mod.Name} from branch {mod.Branch} to {check.Branch}?\n\n" +
            $"Commit: {check.Commit}\n\n" +
            "The distro backs up the mod's settings and database first, keeps your private config, " +
            "and runs the mod's migration. If the switch fails, the code returns to the current branch, " +
            "but database changes made by the migration are not rolled back.",
            "Switch custom mod branch",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes)
        {
            await RefreshCustomModsAsync(checkHealth: false).ConfigureAwait(true);
            CustomModsStatusText = "Branch switch cancelled.";
            return;
        }

        await RunSelectedCustomModOperationAsync(
            "Switch branch",
            (id, output, cancellationToken) => _customModService.SwitchBranchAsync(id, check.Branch, check.Commit, output, cancellationToken))
            .ConfigureAwait(true);
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
