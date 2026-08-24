using System.Windows;
using System.Windows.Controls;
using DwemerDistro.Launcher.Wpf.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class InstallComponentsView : System.Windows.Controls.UserControl
{
    private bool _initialized;

    public InstallComponentsView()
    {
        InitializeComponent();
    }

    private async void InstallComponentsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || DataContext is not InstallComponentsWindowViewModel viewModel)
        {
            return;
        }

        _initialized = true;
        await viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private async void EditHuggingFaceTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InstallComponentsWindowViewModel viewModel)
        {
            return;
        }

        try
        {
            var window = new HuggingFaceTokenWindow
            {
                Owner = Window.GetWindow(this)
            };

            if (window.ShowDialog() != true)
            {
                return;
            }

            var error = window.ShouldClearToken
                ? await viewModel.ClearHuggingFaceTokenAsync().ConfigureAwait(true)
                : await viewModel.SaveHuggingFaceTokenAsync(window.TokenValue).ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(
                    $"Failed to update Hugging Face token.\n\n{error}",
                    "Hugging Face Token",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (window.ShouldClearToken)
            {
                viewModel.SetHuggingFaceTokenClearedState();
            }
            else
            {
                viewModel.SetHuggingFaceTokenReplacedState();
                await viewModel.RefreshHuggingFaceTokenStatusAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to update Hugging Face token.\n\n{ex.Message}",
                "Hugging Face Token",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
