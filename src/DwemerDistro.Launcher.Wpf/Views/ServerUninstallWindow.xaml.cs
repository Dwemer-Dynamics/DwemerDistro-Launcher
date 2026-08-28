using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>
/// Confirmation gate for a destructive server purge. The dialog only confirms - the caller runs the
/// uninstall and streams progress to the launcher console - so nothing is deleted while this window
/// is open.
///
/// The Uninstall button stays disabled until the typed text is exactly the product's PURGE token, so
/// the action cannot be reached by pressing Enter through the dialog.
/// </summary>
public partial class ServerUninstallWindow : Window
{
    private readonly string _purgeToken;

    public ServerUninstallWindow(ServerProduct product, string displayName)
    {
        InitializeComponent();

        _purgeToken = ServerManagementService.GetPurgeToken(product);

        Title = $"Uninstall {displayName}";
        HeadingTextBlock.Text = $"Uninstall {displayName}";
        WarningTextBlock.Text =
            $"This permanently deletes {displayName}, all its files, and all saved data. This cannot be undone.";

        ConfirmPromptTextBlock.Text = $"Type {_purgeToken} to confirm.";

        AutomationProperties.SetName(UninstallButton, $"Uninstall {displayName} permanently");
        AutomationProperties.SetHelpText(
            UninstallButton,
            $"Permanently deletes {displayName}, all its files, and all saved data. This cannot be undone. " +
            $"Enabled only after {_purgeToken} is typed exactly.");
        AutomationProperties.SetName(ConfirmTokenTextBox, $"Type {_purgeToken} to confirm uninstall");
        AutomationProperties.SetHelpText(
            ConfirmTokenTextBox,
            $"Confirmation phrase. Enter {_purgeToken} exactly, including the hyphen and capital letters.");
    }

    private void ConfirmTokenTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var typed = ConfirmTokenTextBox.Text.Trim();
        var matches = string.Equals(typed, _purgeToken, StringComparison.Ordinal);
        UninstallButton.IsEnabled = matches;
        var validationMessage = string.Empty;
        if (typed.Length > 0)
        {
            validationMessage = matches ? "Confirmation matches." : "Confirmation does not match.";
        }

        ValidationTextBlock.Text = validationMessage;
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!UninstallButton.IsEnabled)
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Enter only commits once the token matches; otherwise it does nothing so a stray Enter in
        // the confirmation field cannot start a purge.
        if (e.Key == Key.Enter && UninstallButton.IsEnabled)
        {
            e.Handled = true;
            DialogResult = true;
            Close();
        }
    }
}
