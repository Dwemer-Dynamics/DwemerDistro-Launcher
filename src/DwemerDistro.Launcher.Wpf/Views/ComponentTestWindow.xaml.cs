using System.IO;
using System.Windows;
using System.Windows.Media;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class ComponentTestWindow : Window
{
    private readonly MainWindowViewModel _owner;
    private readonly string _id;
    private readonly MediaPlayer _player = new();
    private CancellationTokenSource? _test;
    private string? _audioFile;
    private string? _sampleFile;
    private bool _closed;

    public ComponentTestWindow(MainWindowViewModel owner, string id, string title)
    {
        InitializeComponent();
        _owner = owner;
        _id = id;
        Heading.Text = title;
        Description.Text = ComponentQuickTestService.Description(id);
        var speechInput = ComponentQuickTestService.IsSpeechInput(id);
        Play.Visibility = speechInput || id is "lmstudio" or "minime" ? Visibility.Collapsed : Visibility.Visible;
        Sample.Content = speechInput ? "Transcribe recording" : id == "lmstudio" ? "Send test prompt" : id == "minime" ? "Classify text" : "Generate speech";
        ChooseFile.Visibility = FileLabel.Visibility = speechInput ? Visibility.Visible : Visibility.Collapsed;
        Input.Visibility = InputLabel.Visibility = !speechInput && ComponentQuickTestService.HasSample(id) ? Visibility.Visible : Visibility.Collapsed;
        Sample.IsEnabled = ComponentQuickTestService.HasSample(id);
        Sample.ToolTip = Sample.IsEnabled ? "Runs one local sample request." : "Only service checks are supported for this component.";
        _player.MediaFailed += (_, _) => { if (!_closed) Result.Text = "Audio was generated but could not be played on this PC."; };
        Closed += (_, _) => { _closed = true; _test?.Cancel(); ClearSample(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; CloseOrCancel(); } };
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "WAV recordings (*.wav)|*.wav", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) { _audioFile = picker.FileName; FileLabel.Text = Path.GetFileName(_audioFile); }
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await RunAsync(false);
    private async void Sample_Click(object sender, RoutedEventArgs e) => await RunAsync(true);

    // One operation per popup; cancellation and timeout release the owner's maintenance lease.
    private async Task RunAsync(bool sample)
    {
        if (_test is not null) return;
        using var test = new CancellationTokenSource();
        _test = test;
        ClearSample();
        Check.IsEnabled = Sample.IsEnabled = ChooseFile.IsEnabled = Input.IsEnabled = false;
        CloseButton.Content = "Cancel";
        Result.Text = sample ? "Running sample test…" : "Checking service…";
        try
        {
            var result = await _owner.TestComponentAsync(_id, sample, Input.Text, _audioFile, test.Token);
            if (_closed) return;
            Result.Text = result.Message;
            if (result.Audio is { } audio)
            {
                _sampleFile = Path.Combine(Path.GetTempPath(), "dwemer-component-test-" + Guid.NewGuid().ToString("N") + ".wav");
                test.Token.ThrowIfCancellationRequested();
                File.WriteAllBytes(_sampleFile, audio);
                if (_closed) { ClearSample(); return; }
                Play.IsEnabled = true;
            }
        }
        catch (OperationCanceledException) { if (!_closed) Result.Text = "Test cancelled. The service may still finish the current request."; }
        catch (System.Net.Http.HttpRequestException) { if (!_closed) Result.Text = "Could not reach the local service. Start it and check its port, then retry."; }
        catch (InvalidOperationException error) { if (!_closed) Result.Text = error.Message; }
        catch (Exception) { if (!_closed) Result.Text = "The test could not read the service response. Check the component's logs and configuration."; }
        finally
        {
            if (_closed || test.IsCancellationRequested) ClearSample();
            _test = null;
            if (!_closed)
            {
                Check.IsEnabled = ChooseFile.IsEnabled = Input.IsEnabled = true;
                Sample.IsEnabled = ComponentQuickTestService.HasSample(_id);
                CloseButton.Content = "Close";
            }
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_sampleFile is null) return;
        _player.Open(new Uri(_sampleFile));
        _player.Play();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseOrCancel();
    private void CloseOrCancel() { if (_test is { } test) test.Cancel(); else Close(); }

    private void ClearSample()
    {
        _player.Close();
        Play.IsEnabled = false;
        if (_sampleFile is null) return;
        try { File.Delete(_sampleFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _sampleFile = null;
    }
}
