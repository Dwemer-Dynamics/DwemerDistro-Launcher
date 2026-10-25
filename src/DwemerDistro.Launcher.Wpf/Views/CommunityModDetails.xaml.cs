using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class CommunityModDetails : System.Windows.Controls.UserControl, IDisposable
{
    private readonly CommunityModService _service;
    private readonly Func<bool> _beginInstall;
    private readonly Action _endInstall;
    private readonly Func<Task> _afterInstall;
    private CommunityRelease? _release;
    private bool _busy;
    private bool _installing;
    private bool _disposed;
    private CancellationTokenSource _cancellation = new();

    public CommunityModDetails(CommunityModService service, Func<bool> beginInstall, Action endInstall, Func<Task> afterInstall)
    {
        InitializeComponent();
        _service = service;
        _beginInstall = beginInstall;
        _endInstall = endInstall;
        _afterInstall = afterInstall;
    }

    public bool InstallAttempted { get; private set; }
    public bool IsInstalling => _installing;
    public event EventHandler? BackRequested;

    public async Task OpenAsync(string? repository)
    {
        RepositoryBox.Text = repository ?? string.Empty;
        RepositoryEditor.IsExpanded = RepositoryBox.Text.Length == 0;
        if (RepositoryBox.Text.Length != 0) await CheckAsync();
    }

    private void SetBusy(bool busy, string message)
    {
        if (_disposed) return;
        if (busy) { _cancellation.Dispose(); _cancellation = new CancellationTokenSource(); }
        _busy = busy;
        CheckButton.IsEnabled = FolderButton.IsEnabled = TrustBox.IsEnabled = !_busy;
        BackButton.IsEnabled = !_installing;
        RepositoryBox.IsReadOnly = GameFolderBox.IsReadOnly = _busy;
        InstallButton.IsEnabled = !_busy && _release is not null;
        SourceButton.IsEnabled = NotesButton.IsEnabled = !_busy && _release is not null;
        EvidenceButton.IsEnabled = !_busy && _release?.Manifest["testing"]?["evidence_path"] is not null;
        BusyProgress.IsIndeterminate = _busy;
        BusyProgress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message;
    }

    private async void Repository_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_busy) { e.Handled = true; await CheckAsync(); }
    }

    private void Repository_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _release = null;
        if (InstallButton is not null) InstallButton.IsEnabled = false;
        if (TrustBox is not null) TrustBox.IsChecked = false;
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        if (_busy || _disposed) return;
        SetBusy(true, "Checking the latest installable release…");
        try
        {
            _release = await _service.CheckAsync(RepositoryBox.Text.Trim(), _cancellation.Token);
            if (_disposed) return;
            ModTitle.Text = _release.Name;
            AuthorText.Text = "By " + new Uri(_release.Repository).AbsolutePath.Trim('/').Split('/')[0];
            DescriptionText.Text = CommunityModService.Text(_release.Manifest, "description");
            FeatureList.ItemsSource = (_release.Manifest["features"] as System.Text.Json.Nodes.JsonArray)?.Select(f => "•  " + f!.GetValue<string>()).ToArray();
            ReleaseText.Text = CommunityModService.Text(_release.Game, "name") + " · " + _release.Version;
            CompatibilityText.Text = "Declared game build: " + CommunityModService.Text(_release.Game, "version") +
                "\nLoader: " + CommunityModService.Text(_release.Game, "loader") +
                "\nSource commit: " + _release.Commit +
                "\nAuthor-reported testing: " + (_release.Manifest["testing"]?["summary"]?.GetValue<string>() ?? "Not provided.");
            GameFolderBox.Text = CommunityModService.DetectGame(_release) ?? string.Empty;
            InstallPlanText.Text = "Client: " + CommunityModService.Text(_release.Install, "path") +
                "\nServer: /var/www/html/" + (CommunityModService.Text(_release.Manifest, "server_folder") is { Length: > 0 } folder ? folder : "custom-mods/" + _release.Id) +
                "\nExisting private data is kept.";
            EnableInGameText.Text = CommunityModService.Text(_release.Game, "enable_in_game");
            BannerImage.Source = IconImage.Source = null;
            foreach (var kind in new[] { "icon", "banner" })
            {
                try
                {
                    var bytes = await _service.ArtworkAsync(_release, kind, _cancellation.Token);
                    if (_disposed) return;
                    if (bytes is null) continue;
                    using var stream = new MemoryStream(bytes);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth > 4096 || decoder.Frames[0].PixelHeight > 4096) continue;
                    stream.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
                    var frame = decoder.Frames[0];
                    var scale = Math.Min(1, Math.Min((kind == "icon" ? 64d : 900d) / frame.PixelWidth, (kind == "icon" ? 64d : 360d) / frame.PixelHeight));
                    image.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
                    image.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
                    image.EndInit(); image.Freeze();
                    if (kind == "icon") IconImage.Source = image; else BannerImage.Source = image;
                }
                catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or NotSupportedException or FormatException or ArgumentException or InvalidOperationException) { /* Optional artwork keeps its placeholder. */ }
            }
            SetBusy(false, "Ready to install.");
        }
        catch (Exception error) { _release = null; SetBusy(false, error.Message); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Select the game's installation folder", Multiselect = false };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) GameFolderBox.Text = picker.FolderName;
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
        if (_busy || _disposed || _release is null) return;
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
        finally
        {
            _installing = false;
            _endInstall();
            BackButton.IsEnabled = true;
            await _afterInstall();
        }
    }

    public void Dispose()
    {
        if (_installing || _disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
        _service.Dispose();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!_installing) BackRequested?.Invoke(this, EventArgs.Empty);
    }
}
