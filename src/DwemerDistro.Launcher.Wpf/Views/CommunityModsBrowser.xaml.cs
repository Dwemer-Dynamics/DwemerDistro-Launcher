using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class CommunityModsBrowser : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<CommunityModCard> _cards = [];
    private readonly Dictionary<string, (string Title, ImageSource? Artwork)> _artwork = new(StringComparer.Ordinal);
    private CancellationTokenSource? _search;
    private bool _loaded;

    public CommunityModsBrowser()
    {
        InitializeComponent();
        ModCards.ItemsSource = _cards;
    }

    public event EventHandler<string>? RepositoryRequested;

    private async void Browser_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && !_loaded) await LoadAsync();
        else if (!IsVisible) _search?.Cancel();
    }

    private void Browser_Unloaded(object sender, RoutedEventArgs e) => _search?.Cancel();
    private async void Search_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadAsync();
    }

    private void Mod_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CommunityModCard card)
            RepositoryRequested?.Invoke(this, card.Repository);
    }

    // Catalog requests stay on Windows. A new search cancels old artwork work as well as discovery.
    private async Task LoadAsync()
    {
        _search?.Cancel();
        using var search = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _search = search;
        _loaded = false;
        LoadingProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Loading mods…";
        using var service = new CommunityModService(new WslService(new ProcessRunner()));
        try
        {
            var result = await service.BrowseAsync(SearchBox.Text, search.Token);
            search.Token.ThrowIfCancellationRequested();
            _cards.Clear();
            foreach (var repository in result.Items)
            {
                var card = new CommunityModCard(repository);
                if (_artwork.TryGetValue(card.Repository, out var cached))
                {
                    card.Title = cached.Title;
                    card.Artwork = cached.Artwork;
                }
                _cards.Add(card);
            }
            StatusText.Text = _cards.Count == 0
                ? "No matching mods. Try another name."
                : result.Notice;
            // Titles are usable immediately; at most three release/artwork lookups run at once.
            using var slots = new SemaphoreSlim(3);
            await Task.WhenAll(_cards.Where(card => !_artwork.ContainsKey(card.Repository)).Select(async card =>
            {
                await slots.WaitAsync(search.Token);
                try
                {
                    var release = await service.CheckAsync(card.Repository, search.Token);
                    search.Token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(release.Name) && release.Name.Length <= 60) card.Title = release.Name;
                    ImageSource? artwork = null;
                    foreach (var kind in new[] { "icon", "banner" })
                    {
                        try
                        {
                            var bytes = await service.ArtworkAsync(release, kind, search.Token);
                            search.Token.ThrowIfCancellationRequested();
                            if (bytes is null) continue;
                            using var stream = new MemoryStream(bytes);
                            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                            if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth > 4096 || decoder.Frames[0].PixelHeight > 4096) continue;
                            stream.Position = 0;
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = stream;
                            var frame = decoder.Frames[0];
                            var scale = Math.Min(1, Math.Min(512d / frame.PixelWidth, 512d / frame.PixelHeight));
                            bitmap.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
                            bitmap.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
                            bitmap.EndInit();
                            bitmap.Freeze();
                            artwork = bitmap;
                            break;
                        }
                        catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException
                            or FormatException or ArgumentException or InvalidOperationException)
                        {
                            // A missing or invalid icon still permits the author's banner.
                        }
                    }
                    search.Token.ThrowIfCancellationRequested();
                    card.Artwork = artwork;
                    if (_artwork.Count >= 60) _artwork.Clear();
                    _artwork[card.Repository] = (card.Title, artwork);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException
                    or InvalidOperationException or ArgumentException or NotSupportedException or FormatException
                    or KeyNotFoundException or NullReferenceException)
                {
                    // Missing releases or optional artwork keep a selectable title and placeholder.
                }
                finally { slots.Release(); }
            }));
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_search, search)) StatusText.Text = "Loading stopped. Search again to retry.";
        }
        catch (Exception error)
        {
            if (ReferenceEquals(_search, search)) StatusText.Text = error.Message;
        }
        finally
        {
            if (ReferenceEquals(_search, search))
            {
                _search = null;
                LoadingProgress.Visibility = Visibility.Collapsed;
            }
        }
    }
}

public sealed class CommunityModCard(CommunityRepository repository) : ObservableObject
{
    private string _title = repository.Name.Split('/').Last();
    private ImageSource? _artwork;
    public string Repository => repository.Repository;
    public string Author => repository.Name.Split('/')[0];
    public string Description => repository.Description;
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public ImageSource? Artwork { get => _artwork; set => SetProperty(ref _artwork, value); }
}
