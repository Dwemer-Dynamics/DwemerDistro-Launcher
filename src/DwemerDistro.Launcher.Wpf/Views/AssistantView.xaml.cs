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

        _isNarrow = null;
        ApplyLayout(ActualWidth);
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
        else if (e.PropertyName == nameof(AssistantViewModel.IsSidePanelVisible))
        {
            _isNarrow = null;
            ApplyLayout(ActualWidth);
        }
        else if (e.PropertyName == nameof(AssistantViewModel.HasFileRequest) && _viewModel?.HasFileRequest == true)
        {
            AccessCard.BringIntoView();
            RaiseLiveRegion(AccessCard);
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

    // The message as it is at the click, including a reply that is still streaming.
    private void CopyReply_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AssistantTranscriptItem item)
        {
            CopyText(item.Text, "Message copied.");
        }
    }

    private void CopyConversation_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            CopyText(AssistantViewModel.FormatTranscript(viewModel.Transcript), "Conversation copied.");
        }
    }

    // Only on a click, and the copied text is never logged; the outcome is shown and announced beside Copy conversation.
    private void CopyText(string text, string copied)
    {
        if (text.Length == 0)
        {
            ShowCopyFeedback("Nothing to copy yet.", failed: false);
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowCopyFeedback(copied, failed: false);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowCopyFeedback("Could not copy: another app is using the clipboard. Try again, or select the text and press Ctrl+C.", failed: true);
        }
    }

    private void ShowCopyFeedback(string message, bool failed)
    {
        CopyFeedback.Text = message;
        CopyFeedback.ToolTip = message;
        if (failed)
        {
            CopyFeedback.SetResourceReference(TextBlock.ForegroundProperty, "GameCenter.DangerText");
        }
        else
        {
            CopyFeedback.ClearValue(TextBlock.ForegroundProperty);
        }

        RaiseLiveRegion(CopyFeedback);
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
        // Wide: the conversation takes the full width until there is a review or live-change card to show.
        var side = _viewModel?.IsSidePanelVisible == true;
        SideColumn.Width = narrow || !side ? new GridLength(0) : new GridLength(320);
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
