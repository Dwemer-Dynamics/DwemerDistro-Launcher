using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class AssistantView : UserControl
{
    // Below this width the task and review column moves under the conversation.
    private const double NarrowWidth = 860;
    private AssistantViewModel? _viewModel;
    private bool? _isNarrow;
    // Below this width the review column is not offered wider.
    private const double WideReviewWidth = 560;
    private const double ReviewWidth = 320;
    // What is shown for each transcript item; every key is subscribed to PropertyChanged.
    private readonly Dictionary<AssistantTranscriptItem, TranscriptEntry> _transcriptRuns = new();
    // The pending check that a Find match is still in view once layout settles.
    private DispatcherOperation? _findReveal;

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

        // A count is only true for the messages it was taken over.
        if (FindStatusText.Text.Length > 0) FindStatusText.Text = "The messages shown changed. Press Next to search them again.";
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

    // A match Find selected counts as in use while Find is open, so streaming does not scroll it away.
    private bool IsTranscriptInUse() =>
        TranscriptBox.IsKeyboardFocusWithin || TranscriptBox.IsMouseCaptureWithin || FindBar.Visibility == Visibility.Visible
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

    // Keys only reach this handler while focus is inside the Assistant page. Ctrl+F opens Find; Esc in Find closes Find and
    // never stops a response, and anywhere else on the page it keeps stopping one.
    private void AssistantView_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenFind();
            return;
        }

        if (e.Key == Key.Escape && FindBar.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            CloseFind();
            return;
        }

        if (e.Key != Key.Escape || ViewModel is not { State: AssistantState.Streaming } viewModel)
        {
            return;
        }

        e.Handled = true;
        _ = viewModel.InterruptAsync();
    }

    // --- Find in the messages shown ---------------------------------------------------------------

    private void OpenFind_Click(object sender, RoutedEventArgs e) => OpenFind();
    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext(forward: true);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindNext(forward: false);
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    internal void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0) FindNext(forward: true, fromCurrent: true);
    }

    /// <summary>Hides Find; the last match stays selected in the transcript, which takes focus back.</summary>
    internal void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindStatusText.Text = string.Empty;
        TranscriptBox.Focus();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => FindNext(forward: true, fromCurrent: true);

    // Enter finds the next match and Shift+Enter the previous; nothing is typed or sent anywhere else.
    private void FindBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.F3)) return;
        e.Handled = true;
        FindNext(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
    }

    /// <summary>
    /// Selects the next (or previous) exact match after the current selection, wrapping around, with the selection's own
    /// TextRange; the count and the label say it covers only the messages loaded here.
    /// </summary>
    internal void FindNext(bool forward, bool fromCurrent = false)
    {
        var query = FindBox.Text;
        var shown = _viewModel?.Transcript.Count ?? 0;
        var window = _viewModel?.IsBrowsingHistory == true ? $"the {shown} earlier message(s) loaded here" : $"the {shown} message(s) loaded here";
        if (query.Length == 0)
        {
            FindStatusText.Text = $"Type to search {window}.";
            return;
        }

        var matches = TranscriptFind.Matches(TranscriptBox.Document, query);
        if (matches.Count == 0)
        {
            FindStatusText.Text = $"Not found in {window}." + (_viewModel?.HasEarlierHistory == true ? " Load earlier messages to search further back." : string.Empty);
            return;
        }

        var anchor = TranscriptBox.Selection.Start;
        int index;
        if (forward)
        {
            index = matches.FindIndex(match => fromCurrent ? match.Start.CompareTo(anchor) >= 0 : match.Start.CompareTo(anchor) > 0);
            if (index < 0) index = 0;
        }
        else
        {
            index = matches.FindLastIndex(match => match.Start.CompareTo(anchor) < 0);
            if (index < 0) index = matches.Count - 1;
        }

        var found = matches[index];
        TranscriptBox.Selection.Select(found.Start, found.End);
        RevealMatch(found);
        var count = matches.Count >= TranscriptFind.MaxMatches ? $"{TranscriptFind.MaxMatches}+" : matches.Count.ToString();
        FindStatusText.Text = $"{index + 1} of {count} in {window}. Export chat saves the whole conversation.";
    }

    /// <summary>
    /// Scrolls the selected match into the transcript's viewport without moving focus: once now, after any queued scroll to
    /// the end and pending layout are applied, and again once layout settles, while the selection is still that match.
    /// </summary>
    private void RevealMatch(TextRange found)
    {
        _findReveal?.Abort();
        ScrollMatchIntoView(found);
        var attempts = 0;
        void Recheck()
        {
            _findReveal = null;
            // A newer search, or the reader's own selection, wins.
            if (TranscriptBox.Selection.Start.CompareTo(found.Start) != 0 || TranscriptBox.Selection.End.CompareTo(found.End) != 0) return;
            if (!ScrollMatchIntoView(found) && ++attempts < 10)
                _findReveal = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Recheck));
        }
        _findReveal = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Recheck));
    }

    // True once the match's first character lies inside the viewport; false while the document is not laid out there yet.
    private bool ScrollMatchIntoView(TextRange found)
    {
        TranscriptBox.UpdateLayout();
        if (TranscriptBox.Template?.FindName("PART_ContentHost", TranscriptBox) is not ScrollViewer viewer || viewer.ViewportHeight <= 0) return false;
        for (var pass = 0; pass < 2; pass++)
        {
            var rect = found.Start.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) return false;
            var top = TranscriptBox.TranslatePoint(rect.TopLeft, viewer).Y;
            if (top >= 0 && top + rect.Height <= viewer.ViewportHeight) return true;
            if (pass == 1) return false;
            viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset + top - viewer.ViewportHeight / 3, 0, viewer.ScrollableHeight));
            TranscriptBox.UpdateLayout();
        }
        return false;
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

    // The History list is read on first open (or by Connect); nothing is read while the page only loads.
    private void HistoryToggle_Checked(object sender, RoutedEventArgs e) => ViewModel?.OpenHistory();

    // Opening Memory reads Core's notes and settings once; Refresh reads them again.
    private void MemoryToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Memory is { } memory && memory.RefreshCommand.CanExecute(null)) memory.RefreshCommand.Execute(null);
    }

    // A memory note starts from the selected conversation text, or the last reply; the user edits it before Remember.
    private void RememberSelection_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        var text = TranscriptBox.Selection.IsEmpty
            ? viewModel.Transcript.LastOrDefault(item => item.Role is "Codex" or "You")?.Text ?? string.Empty
            : TranscriptBox.Selection.Text;
        viewModel.Memory.BeginNote(text);
        MemoryToggle.IsChecked = true;
        MemoryPanel.BringIntoView();
    }

    private void WiderReviewToggle_Changed(object sender, RoutedEventArgs e)
    {
        _isNarrow = null;
        ApplyLayout(ActualWidth);
    }

    private void AssistantView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyLayout(e.NewSize.Width);
        FitPanels(e.NewSize.Height);
    }

    // Wide: open History and Memory share at most about a third of the page and scroll, so the conversation stays usable.
    // Narrow: the page itself scrolls, so the panels show in full.
    internal void FitPanels(double height) =>
        PanelScroll.MaxHeight = _isNarrow == true || height <= 0 ? double.PositiveInfinity : Math.Max(180, height * 0.35);

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
        ChatCard.Height = narrow ? 380 : double.NaN;
        SessionActions.HorizontalAlignment = narrow ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
        Grid.SetColumn(SessionActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(SessionActions, narrow ? 2 : 1);
        Grid.SetRow(SessionActions, narrow ? 1 : 0);
        SessionActions.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        PageScroll.VerticalScrollBarVisibility = narrow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
}
