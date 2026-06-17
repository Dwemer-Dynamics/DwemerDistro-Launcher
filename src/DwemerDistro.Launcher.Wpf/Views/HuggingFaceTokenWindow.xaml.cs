using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class HuggingFaceTokenWindow : Window
{
    public HuggingFaceTokenWindow()
    {
        InitializeComponent();
    }

    public string TokenValue { get; private set; } = string.Empty;

    public bool ShouldClearToken { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        TokenValue = GetCurrentTokenText();
        if (string.IsNullOrWhiteSpace(TokenValue))
        {
            MessageBox.Show(
                "Enter a Hugging Face token, or use Clear to remove the stored token.",
                "Hugging Face Token",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ShouldClearToken = false;
        DialogResult = true;
        Close();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = MessageBox.Show(
            "Clear the Hugging Face token stored in WSL?",
            "Clear Hugging Face Token",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        TokenValue = string.Empty;
        ShouldClearToken = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowTokenCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        TokenTextBox.Text = TokenPasswordBox.Password;
        TokenPasswordBox.Visibility = Visibility.Collapsed;
        TokenTextBox.Visibility = Visibility.Visible;
        TokenTextBox.Focus();
        TokenTextBox.CaretIndex = TokenTextBox.Text.Length;
    }

    private void ShowTokenCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        TokenPasswordBox.Password = TokenTextBox.Text;
        TokenTextBox.Visibility = Visibility.Collapsed;
        TokenPasswordBox.Visibility = Visibility.Visible;
        TokenPasswordBox.Focus();
    }

    private string GetCurrentTokenText()
    {
        return TokenTextBox.Visibility == Visibility.Visible
            ? TokenTextBox.Text.Trim()
            : TokenPasswordBox.Password.Trim();
    }
}
