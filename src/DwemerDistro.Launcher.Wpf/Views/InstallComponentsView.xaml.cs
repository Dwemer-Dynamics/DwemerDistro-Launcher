using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DwemerDistro.Launcher.Wpf.ViewModels;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class InstallComponentsView : System.Windows.Controls.UserControl
{
    // Below this page width the section links move above the cards and the token actions wrap.
    // It matches the point where the side links would force one card column, so narrowing never adds a column back.
    private const double CompactPageWidth = SectionLinksWidth + SectionLinksGap + SingleCardColumnWidth;
    // Below this card-area width each card takes the full row.
    private const double SingleCardColumnWidth = 480;
    // Below this page width the NVIDIA and AMD tiles stack.
    private const double StackedAccessWidth = 480;
    private const double SectionLinksWidth = 156;
    private const double SectionLinksGap = 16;

    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(
        nameof(CardColumns), typeof(int), typeof(InstallComponentsView), new PropertyMetadata(2));

    public static readonly DependencyProperty SectionLinksOrientationProperty = DependencyProperty.Register(
        nameof(SectionLinksOrientation), typeof(Orientation), typeof(InstallComponentsView), new PropertyMetadata(Orientation.Vertical));

    public static readonly DependencyProperty SectionLinkWidthProperty = DependencyProperty.Register(
        nameof(SectionLinkWidth), typeof(double), typeof(InstallComponentsView), new PropertyMetadata(SectionLinksWidth));

    private bool _initialized;
    private bool _isCompact;

    public InstallComponentsView()
    {
        InitializeComponent();
    }

    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    public Orientation SectionLinksOrientation
    {
        get => (Orientation)GetValue(SectionLinksOrientationProperty);
        set => SetValue(SectionLinksOrientationProperty, value);
    }

    public double SectionLinkWidth
    {
        get => (double)GetValue(SectionLinkWidthProperty);
        set => SetValue(SectionLinkWidthProperty, value);
    }

    private async void InstallComponentsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || DataContext is not InstallComponentsWindowViewModel viewModel)
        {
            return;
        }

        _initialized = true;
        await viewModel.AttachAsync().ConfigureAwait(true);
    }

    // Runs only when the page width changes, never per frame. Narrowing never adds a card column. Only the
    // compact step makes the page shorter, and the window's minimum width keeps the page above that step.
    private void PageContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ApplyResponsiveLayout(e.NewSize.Width);
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        _isCompact = width < CompactPageWidth;
        var stacked = width < StackedAccessWidth;

        Grid.SetColumnSpan(NvidiaTile, stacked ? 3 : 1);
        Grid.SetRow(AmdTile, stacked ? 1 : 0);
        Grid.SetColumn(AmdTile, stacked ? 0 : 2);
        Grid.SetColumnSpan(AmdTile, stacked ? 3 : 1);
        AmdTile.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(0);

        Grid.SetRow(HuggingFaceActions, _isCompact ? 1 : 0);
        Grid.SetColumn(HuggingFaceActions, _isCompact ? 1 : 2);
        HuggingFaceActions.Margin = _isCompact ? new Thickness(10, 2, 0, 0) : new Thickness(0);

        LinksColumn.Width = new GridLength(_isCompact ? 0 : SectionLinksWidth);
        LinksGapColumn.Width = new GridLength(_isCompact ? 0 : SectionLinksGap);
        Grid.SetColumnSpan(SectionLinks, _isCompact ? 3 : 1);
        SectionLinks.Margin = _isCompact ? new Thickness(0, 0, 0, 12) : new Thickness(0);
        Grid.SetRow(SectionList, _isCompact ? 1 : 0);
        Grid.SetColumn(SectionList, _isCompact ? 0 : 2);
        Grid.SetColumnSpan(SectionList, _isCompact ? 3 : 1);
        SectionLinksOrientation = _isCompact ? Orientation.Horizontal : Orientation.Vertical;
        SectionLinkWidth = _isCompact ? double.NaN : SectionLinksWidth;

        var cardAreaWidth = _isCompact ? width : width - SectionLinksWidth - SectionLinksGap;
        CardColumns = cardAreaWidth < SingleCardColumnWidth ? 1 : 2;
        UpdateSectionLinksOffset();
    }

    private void PageScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateSectionLinksOffset();
    }

    // Keeps the side links beside the cards while the page scrolls. Only the render offset moves,
    // so this never triggers another layout pass or scroll event.
    private void UpdateSectionLinksOffset()
    {
        if (_isCompact || !BrowserGrid.IsArrangeValid)
        {
            SectionLinksOffset.Y = 0;
            return;
        }

        var browserTop = BrowserGrid.TranslatePoint(new System.Windows.Point(0, 0), PageContent).Y;
        var maxOffset = Math.Max(0, BrowserGrid.ActualHeight - SectionLinks.ActualHeight);
        SectionLinksOffset.Y = Math.Clamp(PageScroller.VerticalOffset - browserTop, 0, maxOffset);
    }

    // Section links scroll to a heading and mark it current; they never filter or hide cards.
    private void SectionLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstallComponentSectionViewModel section }
            || DataContext is not InstallComponentsWindowViewModel viewModel)
        {
            return;
        }

        foreach (var candidate in viewModel.Sections)
        {
            candidate.IsCurrent = ReferenceEquals(candidate, section);
        }

        if (SectionList.ItemContainerGenerator.ContainerFromItem(section) is not FrameworkElement container)
        {
            return;
        }

        // Keyboard users continue from the section they picked instead of tabbing through earlier cards.
        // Focus moves first so its bring-into-view does not override the heading scroll below.
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            FindFirstEnabledButton(container)?.Focus();
        }

        var sectionTop = container.TranslatePoint(new System.Windows.Point(0, 0), PageContent).Y;
        PageScroller.ScrollToVerticalOffset(Math.Max(0, sectionTop));
    }

    private static System.Windows.Controls.Button? FindFirstEnabledButton(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is UIElement { Visibility: not Visibility.Visible })
            {
                continue;
            }

            if (child is System.Windows.Controls.Button { IsEnabled: true } button)
            {
                return button;
            }

            if (FindFirstEnabledButton(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
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
