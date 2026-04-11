using System.Windows;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.ViewModels;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class RollbackWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly string _serverKey;
    private readonly string _displayName;

    public RollbackWindow(
        MainWindowViewModel viewModel,
        string serverKey,
        string displayName,
        string? currentBranch,
        string? currentSha,
        IReadOnlyList<RollbackTarget> targets)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _serverKey = serverKey;
        _displayName = displayName;

        Title = $"Rollback {displayName}";
        TitleTextBlock.Text = $"{displayName} Rollback";
        CurrentTextBlock.Text = $"Current: {currentBranch ?? "unknown"} @ {currentSha ?? "unknown"}";

        TargetsListBox.ItemsSource = targets;
        if (targets.Count > 0)
        {
            TargetsListBox.SelectedIndex = 0;
        }
    }

    private async void RollbackButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RequestRollbackAsync(_serverKey, _displayName, TargetsListBox.SelectedItem as RollbackTarget, this);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
