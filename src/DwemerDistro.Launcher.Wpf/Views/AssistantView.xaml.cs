using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using DwemerDistro.Launcher.Wpf.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class AssistantView : UserControl
{
    // Below this width the task and review column moves under the conversation.
    private const double NarrowWidth = 860;
    private AssistantViewModel? _viewModel;
    private bool? _isNarrow;

    public AssistantView()
    {
        InitializeComponent();
        DataContextChanged += AssistantView_DataContextChanged;
    }

    private AssistantViewModel? ViewModel => _viewModel;

    private void AssistantView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.Transcript.CollectionChanged -= Transcript_CollectionChanged;
        }

        _viewModel = e.NewValue as AssistantViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.Transcript.CollectionChanged += Transcript_CollectionChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantViewModel.StatusText))
        {
            RaiseLiveRegion(StatusTextBlock);
        }
        else if (e.PropertyName == nameof(AssistantViewModel.OutcomeText))
        {
            RaiseLiveRegion(OutcomeTextBlock);
        }
    }

    /// <summary>Scrolls the status header into view and announces it, e.g. after a blocked close.</summary>
    internal void RevealStatus()
    {
        PageScroll.ScrollToTop();
        StatusTextBlock.BringIntoView();
        RaiseLiveRegion(StatusTextBlock);
    }

    private static void RaiseLiveRegion(UIElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // Follow the conversation as it grows, including streamed text on the newest message.
    private void Transcript_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (AssistantTranscriptItem item in e.NewItems)
            {
                item.PropertyChanged += TranscriptItem_PropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (AssistantTranscriptItem item in e.OldItems)
            {
                item.PropertyChanged -= TranscriptItem_PropertyChanged;
            }
        }

        TranscriptScroll.ScrollToEnd();
    }

    private void TranscriptItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var atBottom = TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 24;
        if (atBottom)
        {
            TranscriptScroll.ScrollToEnd();
        }
    }

    // Enter sends, Shift+Enter keeps the newline the TextBox would insert anyway.
    private void InputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel?.SendCommand.CanExecute(null) == true)
        {
            ViewModel.SendCommand.Execute(null);
        }
    }

    // Esc only reaches this handler while focus is inside the Assistant page.
    private void AssistantView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel is not { State: AssistantState.Streaming } viewModel)
        {
            return;
        }

        e.Handled = true;
        _ = viewModel.InterruptAsync();
    }

    private void CopySignInCode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { HasSignInCode: true } viewModel)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(viewModel.SignInCode);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another application holds the clipboard; the code stays visible and selectable.
        }
    }

    private void AssistantView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyLayout(e.NewSize.Width);
    }

    internal void ApplyLayout(double width)
    {
        var narrow = width > 0 && width < NarrowWidth;
        if (_isNarrow == narrow)
        {
            return;
        }

        _isNarrow = narrow;
        // Narrow: one column, the page scrolls, and the conversation keeps a usable fixed height.
        SideColumn.Width = narrow ? new GridLength(0) : new GridLength(320);
        Grid.SetColumn(SidePanel, narrow ? 0 : 1);
        Grid.SetRow(SidePanel, narrow ? 1 : 0);
        SidePanel.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(10, 0, 0, 0);
        SidePanel.VerticalScrollBarVisibility = narrow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ChatRow.Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        ChatCard.Height = narrow ? 380 : double.NaN;
        SessionActions.HorizontalAlignment = narrow ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        Grid.SetColumn(SessionActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(SessionActions, narrow ? 2 : 1);
        Grid.SetRow(SessionActions, narrow ? 1 : 0);
        SessionActions.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        PageScroll.VerticalScrollBarVisibility = narrow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
}
