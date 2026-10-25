using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class CommunityModsWindow : Window
{
    private readonly CommunityModService _service;
    private readonly Func<bool> _beginInstall;
    private readonly Action _endInstall;
    private CommunityRelease? _release;
    private bool _busy;
    private bool _installing;
    private CancellationTokenSource _cancellation = new();

    public CommunityModsWindow(CommunityModService service, Func<bool> beginInstall, Action endInstall)
    {
        InitializeComponent();
        _service = service;
        _beginInstall = beginInstall;
        _endInstall = endInstall;
    }

    public bool InstallAttempted { get; private set; }

    public void SetRepository(string repository) => RepositoryBox.Text = repository;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (RepositoryBox.Text.Length == 0) Browse_Click(sender, e); else Check_Click(sender, e);
    }

    private void SetBusy(bool busy, string message)
    {
        if (busy) { _cancellation.Dispose(); _cancellation = new CancellationTokenSource(); }
        _busy = busy;
        BrowseButton.IsEnabled = CheckButton.IsEnabled = FolderButton.IsEnabled = !_busy;
        Repositories.IsEnabled = SearchBox.IsEnabled = !_busy;
        RepositoryBox.IsReadOnly = GameFolderBox.IsReadOnly = _busy;
        InstallButton.IsEnabled = !_busy && _release is not null;
        SourceButton.IsEnabled = NotesButton.IsEnabled = !_busy && _release is not null;
        EvidenceButton.IsEnabled = !_busy && _release?.Manifest["testing"]?["evidence_path"] is not null;
        BusyProgress.IsIndeterminate = _busy;
        BusyProgress.Opacity = _busy ? 1 : 0;
        StatusText.Text = message;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Loading community repositories…");
        try
        {
            var result = await _service.BrowseAsync(SearchBox.Text, _cancellation.Token);
            Repositories.ItemsSource = result.Items;
            SetBusy(false, result.Items.Count == 0 ? "No matching repositories. Try another name or paste a URL." : result.Notice);
        }
        catch (Exception error) { SetBusy(false, error.Message); }
    }

    private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_busy) { e.Handled = true; Browse_Click(sender, e); }
    }

    private void Repository_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_busy && Repositories.SelectedItem is CommunityRepository repository)
        {
            RepositoryBox.Text = repository.Repository;
            Check_Click(sender, e);
        }
    }

    private void Repository_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _release = null;
        if (InstallButton is not null) InstallButton.IsEnabled = false;
        if (TrustBox is not null) TrustBox.IsChecked = false;
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Checking the latest installable release…");
        try
        {
            _release = await _service.CheckAsync(RepositoryBox.Text.Trim(), _cancellation.Token);
            ModTitle.Text = _release.Name;
            DescriptionText.Text = CommunityModService.Text(_release.Manifest, "description") +
                (_release.Manifest["features"] is System.Text.Json.Nodes.JsonArray features ? "\n" + string.Join(" · ", features.Select(f => f!.GetValue<string>())) : string.Empty);
            ReleaseText.Text = CommunityModService.Text(_release.Game, "name") + " · " + _release.Version + " · " + _release.Commit[..7] +
                "\nDeclared game build: " + CommunityModService.Text(_release.Game, "version") +
                "\nLoader: " + CommunityModService.Text(_release.Game, "loader") +
                "\nAuthor-reported testing: " + (_release.Manifest["testing"]?["summary"]?.GetValue<string>() ?? "Not provided.");
            GameFolderBox.Text = CommunityModService.DetectGame(_release) ?? string.Empty;
            InstallPlanText.Text = "Install client under " + CommunityModService.Text(_release.Install, "path") +
                "; server in the distro at /var/www/html/" + (CommunityModService.Text(_release.Manifest, "server_folder") is { Length: > 0 } folder ? folder : "custom-mods/" + _release.Id) +
                ". Existing private data is kept.\n" + CommunityModService.Text(_release.Game, "enable_in_game");
            BannerImage.Source = IconImage.Source = null;
            foreach (var kind in new[] { "icon", "banner" })
            {
                try
                {
                    var bytes = await _service.ArtworkAsync(_release, kind, _cancellation.Token);
                    if (bytes is null) continue;
                    using var stream = new MemoryStream(bytes);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth > 4096 || decoder.Frames[0].PixelHeight > 4096) continue;
                    stream.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
                    image.DecodePixelWidth = kind == "icon" ? 64 : 900;
                    image.EndInit(); image.Freeze();
                    if (kind == "icon") IconImage.Source = image; else BannerImage.Source = image;
                }
                catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or NotSupportedException or FormatException or ArgumentException or InvalidOperationException) { /* Optional artwork keeps its placeholder. */ }
            }
            SetBusy(false, "Review the author and install plan. Installation does not launch the game or test paid providers.");
        }
        catch (Exception error) { _release = null; SetBusy(false, error.Message); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Select the game's installation folder", Multiselect = false };
        if (picker.ShowDialog(this) == true) GameFolderBox.Text = picker.FolderName;
    }

    private void OpenSource(string suffix)
    {
        if (_release is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_release.Repository + suffix) { UseShellExecute = true });
    }
    private void Source_Click(object sender, RoutedEventArgs e) => OpenSource(string.Empty);
    private void Notes_Click(object sender, RoutedEventArgs e) => OpenSource("/releases/tag/" + _release!.Version);
    private void Evidence_Click(object sender, RoutedEventArgs e) => OpenSource("/blob/" + _release!.Commit + "/" + CommunityModService.Relative(_release.Manifest["testing"]!["evidence_path"]!.GetValue<string>()));

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _release is null) return;
        if (TrustBox.IsChecked != true) { StatusText.Text = "Review the plan and confirm that you trust the author."; return; }
        if (!_beginInstall()) { StatusText.Text = "Set up the distro and finish any maintenance before installing."; return; }
        _installing = true;
        InstallAttempted = true;
        SetBusy(true, "Preparing installation…");
        try
        {
            var status = await _service.InstallAsync(_release, GameFolderBox.Text,
                new Progress<string>(message => StatusText.Text = message));
            SetBusy(false, status);
        }
        catch (Exception error) { SetBusy(false, error.Message + " If the server installed, retry this release to finish the client."); }
        finally { _installing = false; _endInstall(); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_installing) { e.Cancel = true; StatusText.Text = "Wait for installation to finish before closing."; }
        else _cancellation.Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
