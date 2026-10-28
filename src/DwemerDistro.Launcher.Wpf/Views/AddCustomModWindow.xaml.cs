using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>
/// URL -> Check repo -> manifest preview -> explicit trust -> install. Install is reachable only for
/// the exact commit the user previewed: the distro refuses if the repository changed since.
/// </summary>
public partial class AddCustomModWindow : Window
{
    private readonly CustomModService _service;
    private readonly Action<string> _consoleOutput;
    private CustomModPreview? _preview;
    private bool _isBusy;
    private bool _isInstalling;
    private CancellationTokenSource? _checkCancellation;

    public AddCustomModWindow(CustomModService service, Action<string> consoleOutput)
    {
        InitializeComponent();
        _service = service;
        _consoleOutput = consoleOutput;
    }

    /// <summary>True once an install ran, even if it failed, so the caller refreshes the list.</summary>
    public bool InstallAttempted { get; private set; }

    private void RepositoryUrlTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Any edit invalidates an earlier preview and trust decision.
        _preview = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
        TrustCheckBox.Visibility = Visibility.Collapsed;
        TrustCheckBox.IsChecked = false;
        var error = CustomModService.ValidateRepositoryUrl(RepositoryUrlTextBox.Text.Trim());
        StatusText.Text = RepositoryUrlTextBox.Text.Length == 0 ? string.Empty : error ?? string.Empty;
        UpdateButtons();
    }

    private void RepositoryUrlTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CheckButton.IsEnabled)
        {
            e.Handled = true;
            CheckButton_Click(sender, e);
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        var url = RepositoryUrlTextBox.Text.Trim();
        if (CustomModService.ValidateRepositoryUrl(url) is not null)
        {
            return;
        }

        // The check only reads the repository as the dwemer user, so Cancel or closing may stop it.
        using var cancellation = new CancellationTokenSource();
        _checkCancellation = cancellation;
        SetBusy(true, "Checking repository…");
        CustomModPreviewResult result;
        try
        {
            result = await _service.CheckAsync(url, null, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            result = new CustomModPreviewResult(null, ex.Message);
        }
        finally
        {
            _checkCancellation = null;
        }

        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        SetBusy(false, string.Empty);
        if (result.Preview is null)
        {
            StatusText.Text = result.Error ?? "The repository could not be checked.";
            return;
        }

        if (result.Preview.Registered)
        {
            StatusText.Text = $"{result.Preview.Name} is already in your Custom mods list.";
            return;
        }

        _preview = result.Preview;
        PreviewNameText.Text = _preview.Name;
        PreviewDescriptionText.Text = _preview.Description;
        PreviewDescriptionText.Visibility = string.IsNullOrEmpty(_preview.Description) ? Visibility.Collapsed : Visibility.Visible;
        PreviewDetailsText.Text = _preview.Restorable
            ? DescribeRestore(_preview)
            : $"ID {_preview.Id} · branch {_preview.Branch} · commit {_preview.Commit[..7]}{Environment.NewLine}" +
              $"Folder {_preview.Folder}{Environment.NewLine}Database {_preview.Database}";
        InstallButton.Content = _preview.Restorable ? "Restore" : "Install";
        PreviewPanel.Visibility = Visibility.Visible;
        TrustCheckBox.Visibility = Visibility.Visible;
        StatusText.Text = "Review the details, then confirm you trust this code.";
        UpdateButtons();
        TrustCheckBox.Focus();
    }

    /// <summary>Restore reattaches the kept installation; it never installs the newer checked commit.</summary>
    internal static string DescribeRestore(CustomModPreview preview)
    {
        var text = $"You removed this mod earlier. Restore puts it back in the list at its installed commit " +
                   $"{preview.InstalledCommit[..7]}, keeping its files, private config, and database as they are." +
                   $"{Environment.NewLine}Folder {preview.Folder}{Environment.NewLine}Database {preview.Database}";
        return preview.InstalledCommit == preview.Commit
            ? text
            : text + $"{Environment.NewLine}The repository now has commit {preview.Commit[..7]}. Choose Update after restoring to install it.";
    }

    private void TrustCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateButtons();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var preview = _preview;
        if (preview is null || TrustCheckBox.IsChecked != true || _isBusy)
        {
            return;
        }

        InstallAttempted = true;
        var verb = preview.Restorable ? "Restoring" : "Installing";
        _isInstalling = true;
        SetBusy(true, $"{verb} {preview.Name}…");
        _consoleOutput($"{verb} custom mod {preview.Name} from {preview.Repository}.");
        Models.CommandResult? result = null;
        string? error = null;
        try
        {
            result = await _service.InstallAsync(preview.Repository, preview.Commit, line =>
            {
                _consoleOutput(line);
                Dispatcher.BeginInvoke(() =>
                {
                    if (line.StartsWith("[INFO] ", StringComparison.Ordinal) || line.StartsWith("[OK] ", StringComparison.Ordinal))
                    {
                        StatusText.Text = line[(line.IndexOf(' ') + 1)..];
                    }
                });
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _isInstalling = false;
        SetBusy(false, string.Empty);
        if (result is { Succeeded: true })
        {
            DialogResult = true;
            Close();
            return;
        }

        StatusText.Text = error ?? (result is null ? $"{(preview.Restorable ? "Restore" : "Install")} failed." : CustomModService.DescribeFailure(result));
        // The checked commit was consumed; the user checks again before another attempt.
        _preview = null;
        TrustCheckBox.IsChecked = false;
        TrustCheckBox.Visibility = Visibility.Collapsed;
        UpdateButtons();
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        BusyProgress.IsIndeterminate = busy;
        BusyProgress.Opacity = busy ? 1 : 0;
        RepositoryUrlTextBox.IsReadOnly = busy;
        if (busy)
        {
            StatusText.Text = status;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var urlValid = CustomModService.ValidateRepositoryUrl(RepositoryUrlTextBox.Text.Trim()) is null;
        CheckButton.IsEnabled = !_isBusy && urlValid;
        TrustCheckBox.IsEnabled = !_isBusy;
        InstallButton.IsEnabled = !_isBusy && _preview is not null && TrustCheckBox.IsChecked == true;
        CancelButton.IsEnabled = !_isInstalling;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isInstalling)
        {
            Close();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // An install or restore runs as root in the distro and keeps going if the window closes,
        // so stay open until it ends. A repository check is read-only and is simply cancelled.
        if (_isInstalling)
        {
            e.Cancel = true;
            StatusText.Text = InstallStillRunningMessage;
            return;
        }

        _checkCancellation?.Cancel();
    }

    internal const string InstallStillRunningMessage =
        "The install is still running in the distro. This window closes when it finishes.";
}
