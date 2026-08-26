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

    public ServerUninstallWindow(
        ServerProduct product,
        string displayName,
        string? root,
        string? database,
        bool? databasePresent)
    {
        InitializeComponent();

        _purgeToken = ServerManagementService.GetPurgeToken(product);

        Title = $"Uninstall {displayName}";
        HeadingTextBlock.Text = $"Uninstall {displayName}";
        SubheadingTextBlock.Text =
            $"This permanently deletes {displayName} and everything it stores in this distro.";

        ServerNameTextBlock.Text = displayName;
        RootPathTextBlock.Text = string.IsNullOrWhiteSpace(root) ? "Path not reported by the distro" : root;
        DatabaseTextBlock.Text = FormatDatabase(database, databasePresent);
        BusyTextBlock.Text = "Progress appears in the launcher console.";

        ConfirmPromptTextBlock.Text = $"Type {_purgeToken} to confirm.";
        ValidationTextBlock.Text = $"Uninstall stays disabled until the text matches {_purgeToken} exactly.";

        AutomationProperties.SetName(UninstallButton, $"Uninstall {displayName} permanently");
        AutomationProperties.SetHelpText(
            UninstallButton,
            $"Deletes {displayName} files, its database, and every profile, memory, setting, log, upload, and voice it stores. " +
            $"Enabled only after {_purgeToken} is typed exactly.");
        AutomationProperties.SetName(ConfirmTokenTextBox, $"Type {_purgeToken} to confirm uninstall");
        AutomationProperties.SetHelpText(
            ConfirmTokenTextBox,
            $"Confirmation phrase. Enter {_purgeToken} exactly, including the hyphen and capital letters.");
        AutomationProperties.SetName(
            ServerNameTextBlock,
            $"Server to uninstall: {displayName}");
        AutomationProperties.SetName(
            RootPathTextBlock,
            $"Files to delete: {RootPathTextBlock.Text}");
        AutomationProperties.SetName(
            DatabaseTextBlock,
            $"Database to delete: {DatabaseTextBlock.Text}");
    }

    private static string FormatDatabase(string? database, bool? databasePresent)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return "Database not reported by the distro";
        }

        return databasePresent switch
        {
            true => database,
            false => $"{database} (already missing)",
            _ => $"{database} (presence unknown)"
        };
    }

    private void ConfirmTokenTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var typed = ConfirmTokenTextBox.Text.Trim();
        var matches = string.Equals(typed, _purgeToken, StringComparison.Ordinal);
        UninstallButton.IsEnabled = matches;
        ValidationTextBlock.Text = matches
            ? $"Confirmed. Uninstall Server will delete {ServerNameTextBlock.Text}."
            : $"Uninstall stays disabled until the text matches {_purgeToken} exactly.";
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
