using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using DwemerDistro.Launcher.Wpf.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class AssistantView : UserControl
{
    // Below this width the task and review column moves under the conversation.
    private const double NarrowWidth = 860;
    private const double NarrowChatHeight = 380;
    private const double NarrowChatWithCardHeight = 700;
    private AssistantViewModel? _viewModel;
    private bool? _isNarrow;
    // Below this width the review column is not offered wider.
    private const double WideReviewWidth = 560;
    private const double ReviewWidth = 320;
    // What is shown for each transcript item; every key is subscribed to PropertyChanged.
    private readonly Dictionary<AssistantTranscriptItem, TranscriptEntry> _transcriptRuns = new();

    /// <summary>
    /// One item's blocks. Plain items (the user's own text, launcher notes, activity) keep one run that streaming appends to;
    /// assistant replies keep their Markdown chunks, so only chunks from the first changed one are rendered again.
    /// </summary>
    private sealed class TranscriptEntry(Section body, Run? plain)
    {
        public Section Body { get; } = body;
        public Run? Plain { get; } = plain;
        public List<string> Chunks { get; } = [];
        public List<Block[]> Rendered { get; } = [];
    }

    public AssistantView()
    {
        InitializeComponent();
        DataContextChanged += AssistantView_DataContextChanged;
        // Ctrl+V in the composer attaches a copied screenshot or copied files; text pastes as usual.
        CommandManager.AddPreviewCanExecuteHandler(InputBox, InputBox_PreviewCanPaste);
        CommandManager.AddPreviewExecutedHandler(InputBox, InputBox_PreviewPaste);
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

        RebuildTranscript();
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
        else if (e.PropertyName == nameof(AssistantViewModel.IsActionProposed))
        {
            // The card sits above the composer; it is announced and given room, but never focused, so typing elsewhere is not interrupted.
            // A transcript followed to its end stays there once the card takes its space.
            var follow = IsFollowingTranscript();
            _isNarrow = null;
            ApplyLayout(ActualWidth);
            if (follow) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => TranscriptBox.ScrollToEnd());
            if (_viewModel?.IsActionProposed == true) RaiseLiveRegion(RestartProposalTitle);
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

    // Shows the current transcript from scratch, so nothing from a previous view model or task stays visible.
    private void RebuildTranscript()
    {
        foreach (var item in _transcriptRuns.Keys)
        {
            item.PropertyChanged -= TranscriptItem_PropertyChanged;
        }

        _transcriptRuns.Clear();
        TranscriptBox.Document.Blocks.Clear();
        if (_viewModel is not null)
        {
            foreach (var item in _viewModel.Transcript)
            {
                TranscriptBox.Document.Blocks.Add(CreateTranscriptBlock(item));
            }
        }

        TranscriptBox.ScrollToEnd();
    }

    private Section CreateTranscriptBlock(AssistantTranscriptItem item)
    {
        item.PropertyChanged += TranscriptItem_PropertyChanged;
        var block = new Section { Tag = item, Margin = new Thickness(0, 0, 0, 10) };
        if (item.Role == AssistantViewModel.ActivityRole)
        {
            // Tool reads and other activity sit between replies, muted and outside the assistant's own text.
            var activity = new Run(item.Text);
            var line = new Paragraph(activity) { Margin = new Thickness(0), FontSize = 11.5, FontStyle = FontStyles.Italic };
            line.SetResourceReference(TextElement.ForegroundProperty, "GameCenter.TextMuted");
            block.Margin = new Thickness(0, 0, 0, 6);
            block.Blocks.Add(line);
            _transcriptRuns[item] = new TranscriptEntry(block, activity);
            return block;
        }

        if (item.Role is AssistantViewModel.OutputRole or AssistantViewModel.CommandRole)
        {
            // A live command's code or redacted output: monospace, selectable with the rest, and output is appended in place as it streams.
            var label = new Run(item.Role) { FontWeight = FontWeights.SemiBold, FontSize = 11 };
            label.SetResourceReference(TextElement.ForegroundProperty, "GameCenter.TextMuted");
            block.Blocks.Add(new Paragraph(label) { Margin = new Thickness(0) });
            var output = new Run(item.Text);
            block.Blocks.Add(new Paragraph(output)
            {
                Margin = new Thickness(0, 2, 0, 0), FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12
            });
            _transcriptRuns[item] = new TranscriptEntry(block, output);
            return block;
        }

        var role = new Run(item.Role) { FontWeight = FontWeights.SemiBold, FontSize = 11 };
        role.SetResourceReference(TextElement.ForegroundProperty, "GameCenter.TextMuted");
        block.Blocks.Add(new Paragraph(role) { Margin = new Thickness(0) });
        if (item.Role != "Codex")
        {
            var text = new Run(item.Text);
            block.Blocks.Add(new Paragraph(text) { Margin = new Thickness(0, 2, 0, 0) });
            _transcriptRuns[item] = new TranscriptEntry(block, text);
            return block;
        }

        var entry = new TranscriptEntry(block, null);
        _transcriptRuns[item] = entry;
        RenderMarkdown(entry, item.Text);
        return block;
    }

    /// <summary>Renders again only from the first chunk that changed; older chunks keep their blocks and any selection in them.</summary>
    private static void RenderMarkdown(TranscriptEntry entry, string text)
    {
        var chunks = AssistantMarkdown.SplitChunks(text);
        var first = 0;
        while (first < chunks.Count && first < entry.Chunks.Count && chunks[first] == entry.Chunks[first]) first++;
        for (var index = entry.Rendered.Count - 1; index >= first; index--)
        {
            foreach (var block in entry.Rendered[index]) entry.Body.Blocks.Remove(block);
            entry.Rendered.RemoveAt(index);
            entry.Chunks.RemoveAt(index);
        }
        for (var index = first; index < chunks.Count; index++)
        {
            var blocks = AssistantMarkdown.RenderChunk(chunks[index]);
            foreach (var block in blocks) entry.Body.Blocks.Add(block);
            entry.Rendered.Add(blocks);
            entry.Chunks.Add(chunks[index]);
        }
    }

    // Adds and removes blocks in place so a selection elsewhere survives; anything else rebuilds.
    private void Transcript_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _viewModel?.Transcript))
        {
            return;
        }

        var follow = IsFollowingTranscript();
        var pinned = PinSelection();
        var blocks = TranscriptBox.Document.Blocks;
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            var before = e.NewStartingIndex >= 0 && e.NewStartingIndex < blocks.Count ? blocks.ElementAt(e.NewStartingIndex) : null;
            foreach (AssistantTranscriptItem item in e.NewItems)
            {
                var block = CreateTranscriptBlock(item);
                if (before is null)
                {
                    blocks.Add(block);
                }
                else
                {
                    blocks.InsertBefore(before, block);
                }
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (AssistantTranscriptItem item in e.OldItems)
            {
                item.PropertyChanged -= TranscriptItem_PropertyChanged;
                _transcriptRuns.Remove(item);
                if (blocks.FirstOrDefault(block => ReferenceEquals(block.Tag, item)) is { } block)
                {
                    blocks.Remove(block);
                }
            }
        }
        else
        {
            RebuildTranscript();
            return;
        }

        RestoreSelection(pinned);
        if (follow)
        {
            TranscriptBox.ScrollToEnd();
        }
    }

    private void TranscriptItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantTranscriptItem.Text) || sender is not AssistantTranscriptItem item
            || !_transcriptRuns.TryGetValue(item, out var entry))
        {
            return;
        }

        var follow = IsFollowingTranscript();
        var pinned = PinSelection();
        if (entry.Plain is not { } run)
        {
            RenderMarkdown(entry, item.Text);
        }
        // Streamed text is appended in place, so a selection inside this message keeps its position.
        else if (item.Text.StartsWith(run.Text, StringComparison.Ordinal))
        {
            run.ContentEnd.InsertTextInRun(item.Text[run.Text.Length..]);
        }
        else
        {
            run.Text = item.Text;
        }

        RestoreSelection(pinned);
        if (follow)
        {
            TranscriptBox.ScrollToEnd();
        }
    }

    // Copies of the selection ends that do not move when text arrives exactly at them, so a selection that
    // ends where the reply is streaming, or a Ctrl+A, keeps the text it had.
    private (TextPointer Start, TextPointer End)? PinSelection()
    {
        var selection = TranscriptBox.Selection;
        return selection.IsEmpty
            ? null
            : (selection.Start.GetPositionAtOffset(0, LogicalDirection.Forward), selection.End.GetPositionAtOffset(0, LogicalDirection.Backward));
    }

    private void RestoreSelection((TextPointer Start, TextPointer End)? pinned)
    {
        var selection = TranscriptBox.Selection;
        // A re-rendered tail moves a pinned end to where it was removed; an end that left the document is not restored.
        if (pinned is { } range && range.Start.IsInSameDocument(TranscriptBox.Document.ContentStart)
            && range.End.IsInSameDocument(TranscriptBox.Document.ContentStart) && range.Start.CompareTo(range.End) <= 0
            && (selection.Start.CompareTo(range.Start) != 0 || selection.End.CompareTo(range.End) != 0))
        {
            selection.Select(range.Start, range.End);
        }
    }

    // Follow the conversation only from the bottom, and never while the reader is selecting in it.
    private bool IsFollowingTranscript()
    {
        var atBottom = TranscriptBox.VerticalOffset >= TranscriptBox.ExtentHeight - TranscriptBox.ViewportHeight - 24;
        return atBottom && (TranscriptBox.Selection.IsEmpty || !IsTranscriptInUse());
    }

    private bool IsTranscriptInUse() =>
        TranscriptBox.IsKeyboardFocusWithin || TranscriptBox.IsMouseCaptureWithin
        || Keyboard.FocusedElement is FrameworkElement focused
        && (focused as System.Windows.Controls.ContextMenu ?? focused.Parent as System.Windows.Controls.ContextMenu)?.PlacementTarget == TranscriptBox;

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

    // --- Attachments: the clipboard and files are read only on the user's own paste, drop or Attach. ---

    private static bool ClipboardHasAttachment()
    {
        try
        {
            return System.Windows.Clipboard.ContainsFileDropList() || System.Windows.Clipboard.ContainsImage() && !System.Windows.Clipboard.ContainsText();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    private void InputBox_PreviewCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ClipboardHasAttachment()) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private void InputBox_PreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || ViewModel is not { } viewModel || !ClipboardHasAttachment()) return;
        e.Handled = true;
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList())
                viewModel.AttachFiles(System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList());
            else if (System.Windows.Clipboard.GetImage() is { } image)
                viewModel.AttachImage(image);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard was busy; the user can paste again.
        }
    }

    private void InputBox_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
    }

    private void InputBox_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        viewModel.AttachFiles(files);
        InputBox.Focus();
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach to the next message",
            Multiselect = true,
            Filter = "Screenshots and text files|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.txt;*.log;*.md;*.json;*.yaml;*.yml;*.toml;*.ini;*.cfg;*.conf;*.xml;*.csv;*.py;*.php;*.js;*.cs;*.sh;*.ps1;*.sql;*.lua;*.psc|All files|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) viewModel.AttachFiles(dialog.FileNames);
        InputBox.Focus();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel && (sender as FrameworkElement)?.DataContext is AssistantAttachment attachment) viewModel.RemoveAttachment(attachment);
        InputBox.Focus();
    }

    // Keys only reach this handler while focus is inside the Assistant page; Esc anywhere on it stops a response.
    private void AssistantView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel is not { } viewModel || !viewModel.StopCommand.CanExecute(null))
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

    private void WiderReviewToggle_Changed(object sender, RoutedEventArgs e)
    {
        _isNarrow = null;
        ApplyLayout(ActualWidth);
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
        var wide = WiderReviewToggle.IsChecked == true && width >= WideReviewWidth * 2;
        // Wider does nothing in one column or below the width it needs, so it is not offered there.
        WiderReviewToggle.Visibility = narrow || width < WideReviewWidth * 2 ? Visibility.Collapsed : Visibility.Visible;
        SideColumn.Width = narrow || !side ? new GridLength(0) : new GridLength(wide ? WideReviewWidth : ReviewWidth);
        Grid.SetColumn(SidePanel, narrow ? 0 : 1);
        Grid.SetRow(SidePanel, narrow ? 1 : 0);
        SidePanel.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(10, 0, 0, 0);
        SidePanel.VerticalScrollBarVisibility = narrow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ChatRow.Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        // A proposal card sits inside the conversation card, so in one column the card grows instead of squeezing the transcript.
        ChatCard.Height = narrow ? (_viewModel?.IsActionProposed == true ? NarrowChatWithCardHeight : NarrowChatHeight) : double.NaN;
        SessionActions.HorizontalAlignment = narrow ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        Grid.SetColumn(SessionActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(SessionActions, narrow ? 2 : 1);
        Grid.SetRow(SessionActions, narrow ? 1 : 0);
        SessionActions.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        PageScroll.VerticalScrollBarVisibility = narrow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
}
