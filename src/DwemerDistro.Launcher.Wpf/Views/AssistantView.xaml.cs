using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
    // The user's messages sit on the right and replies on the left, each leaving at least this share of the width to the other side.
    private const double BubbleGutterShare = 0.18;
    private const double BubbleMinGutter = 40;
    private const double BubbleMaxGutter = 200;
    // Prose stays at a readable line length on wide windows; the rest of the row is left open.
    private const double MaxBubbleWidth = 720;
    private const double BubbleMinWidth = 72;
    // Space between one message and the next.
    private const double MessageGap = 14;
    // Role labels and DwemerDistro's own notes are set smaller than the conversation.
    private const double LabelFontSize = 11;
    private const double NoteFontSize = 12.5;
    // Longer messages simply take the bubble's full width.
    private const int MaxMeasuredChars = 4000;
    // The width bubbles were last fitted to.
    private double _bubbleWidth;
    // Rounded shapes drawn behind the transparent transcript; the document itself stays one selectable FlowDocument.
    private readonly TranscriptBackdrop _backdrop = new();
    private readonly List<TranscriptBackdrop.Shape> _shapes = [];
    private bool _backdropQueued;
    // The shapes last measured, in document coordinates (0 is the top of the document), and the stretch of the document
    // they fully describe; positions are measured again only when something other than the newest message changed.
    private readonly List<(Section Section, TranscriptEntry Entry, double Top, double Bottom)> _measured = [];
    private double _coveredTop;
    private double _coveredBottom = double.NegativeInfinity;
    private double _measuredExtent;
    private double _measuredWidth;
    private bool _remeasure = true;
    // Measures once more after streaming pauses, so the newest shape ends exactly where its text ends.
    private readonly System.Windows.Threading.DispatcherTimer _settle;

    // User and Reply are rounded bubbles, Note is DwemerDistro's own quieter note, Code a live command or its output.
    private enum EntryShape { Plain, User, Reply, Note, Code }

    /// <summary>
    /// One item's blocks. Plain items (the user's own text, launcher notes, activity) keep one run that streaming appends to;
    /// assistant replies keep their Markdown chunks, so only chunks from the first changed one are rendered again.
    /// </summary>
    private sealed class TranscriptEntry(Section body, Run? plain, EntryShape shape = EntryShape.Plain, Paragraph? label = null)
    {
        public Section Body { get; } = body;
        public Run? Plain { get; } = plain;
        public EntryShape Shape { get; } = shape;
        // The role line above the shape; the shape starts with the block after it.
        public Paragraph? Label { get; } = label;
        public List<string> Chunks { get; } = [];
        public List<Block[]> Rendered { get; } = [];
        // The bubble already takes all the width it may; streamed text only grows, so it is not measured again.
        public bool Wide { get; set; }
        // The Activity group this routine item is folded into, if any.
        public ActivityGroup? Group { get; set; }
    }

    /// <summary>
    /// A run of routine activity between messages, behind one toggle. Its items stay in <see cref="Items"/> while it is collapsed,
    /// detached from the document, so they keep streaming in place and reappear exactly when it is opened again.
    /// </summary>
    private sealed class ActivityGroup
    {
        public ActivityGroup(RoutedEventHandler toggled)
        {
            Section = new Section { Tag = this, Margin = new Thickness(0, 0, 0, 10) };
            Toggle = new ToggleButton { Tag = this };
            Toggle.SetResourceReference(StyleProperty, "Assistant.ActivityToggle");
            Toggle.Checked += toggled;
            Toggle.Unchecked += toggled;
            // The chevron lines up with the replies' text; the toggle's own border and padding sit to its left.
            Section.Blocks.Add(new BlockUIContainer(Toggle) { Margin = new Thickness(ShapePadding(EntryShape.Reply).Width - 6, 0, 0, 0) });
        }

        public Section Section { get; }
        public ToggleButton Toggle { get; }
        public Section Items { get; } = new() { Margin = new Thickness(0, 6, 0, 0) };
        public bool IsExpanded => Items.Parent is not null;

        public void Recount()
        {
            var steps = Items.Blocks.Count;
            var label = "Activity · " + steps.ToString(CultureInfo.CurrentCulture) + (steps == 1 ? " step" : " steps");
            Toggle.Content = label;
            AutomationProperties.SetName(Toggle, label);
        }
    }

    /// <summary>
    /// Paints the rounded message shapes behind the transcript. It never takes input or focus and holds no text:
    /// selection, copy, links and scrolling all stay with the RichTextBox in front of it.
    /// </summary>
    private sealed class TranscriptBackdrop : FrameworkElement
    {
        public readonly record struct Shape(Rect Bounds, System.Windows.Media.Brush Fill, double Radius);

        private readonly List<Shape> _shapes = [];
        private Rect _clip;

        public TranscriptBackdrop()
        {
            Name = "TranscriptBackdrop";
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = true;
        }

        public IReadOnlyList<Shape> Shapes => _shapes;

        public void Show(Rect clip, List<Shape> shapes)
        {
            if (clip == _clip && shapes.SequenceEqual(_shapes))
            {
                return;
            }

            _clip = clip;
            _shapes.Clear();
            _shapes.AddRange(shapes);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_shapes.Count == 0 || _clip.IsEmpty)
            {
                return;
            }

            drawingContext.PushClip(new RectangleGeometry(_clip));
            foreach (var shape in _shapes)
            {
                drawingContext.DrawRoundedRectangle(shape.Fill, null, shape.Bounds, shape.Radius, shape.Radius);
            }

            drawingContext.Pop();
        }
    }

    public AssistantView()
    {
        InitializeComponent();
        // Behind the transcript in the same cell, so its shapes share the transcript's coordinates.
        Grid.SetRow(_backdrop, Grid.GetRow(TranscriptBox));
        ChatGrid.Children.Insert(0, _backdrop);
        _settle = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            RemeasureBackdrop();
        };
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
            // Appended in order, so no message ever splits a group here.
            for (var index = 0; index < _viewModel.Transcript.Count; index++)
            {
                PlaceTranscriptBlock(_viewModel.Transcript, index);
            }
        }

        TranscriptBox.ScrollToEnd();
        RemeasureBackdrop();
    }

    /// <summary>
    /// Adds the block of the item at <paramref name="index"/> between its neighbours' blocks. Routine activity joins the Activity group
    /// of the item before or after it, or starts a collapsed one; anything else sits between groups. False, with nothing added, when a
    /// message would split a group in two, which only a rebuild places.
    /// </summary>
    private bool PlaceTranscriptBlock(IList<AssistantTranscriptItem> transcript, int index)
    {
        var item = transcript[index];
        var routine = AssistantViewModel.IsRoutineActivity(item);
        var previous = index > 0 ? _transcriptRuns.GetValueOrDefault(transcript[index - 1]) : null;
        TranscriptEntry? next = null;
        for (var later = index + 1; later < transcript.Count && next is null; later++)
        {
            next = _transcriptRuns.GetValueOrDefault(transcript[later]);
        }

        if (!routine && previous?.Group is { } split && ReferenceEquals(next?.Group, split))
        {
            return false;
        }

        var block = CreateTranscriptBlock(item);
        var top = next is null ? null : (Block)(next.Group?.Section ?? next.Body);
        if (!routine)
        {
            if (top is null) TranscriptBox.Document.Blocks.Add(block);
            else TranscriptBox.Document.Blocks.InsertBefore(top, block);
            return true;
        }

        ActivityGroup group;
        if (previous?.Group is { } earlier)
        {
            group = earlier;
            group.Items.Blocks.InsertAfter(previous.Body, block);
        }
        else if (next?.Group is { } following)
        {
            group = following;
            group.Items.Blocks.InsertBefore(next.Body, block);
        }
        else
        {
            group = new ActivityGroup(ActivityToggle_Changed);
            group.Items.Blocks.Add(block);
            if (top is null) TranscriptBox.Document.Blocks.Add(group.Section);
            else TranscriptBox.Document.Blocks.InsertBefore(top, group.Section);
        }

        _transcriptRuns[item].Group = group;
        group.Recount();
        return true;
    }

    // Opens or folds an Activity group in place; its items keep their text, and a selection outside it is kept.
    private void ActivityToggle_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as ToggleButton)?.Tag is not ActivityGroup group || (group.Toggle.IsChecked == true) == group.IsExpanded)
        {
            return;
        }

        var pinned = PinSelection();
        if (group.IsExpanded) group.Section.Blocks.Remove(group.Items);
        else group.Section.Blocks.Add(group.Items);
        RestoreSelection(pinned);
        RemeasureBackdrop();
    }

    private Section CreateTranscriptBlock(AssistantTranscriptItem item)
    {
        item.PropertyChanged += TranscriptItem_PropertyChanged;
        var block = new Section { Tag = item, Margin = new Thickness(0, 0, 0, MessageGap) };
        if (item.Role == AssistantViewModel.ActivityRole)
        {
            // Activity sits between replies, muted, aligned with the replies' text and outside it; routine steps in a group sit closer,
            // under the group's label.
            var routine = AssistantViewModel.IsRoutineActivity(item);
            var activity = new Run(item.Text);
            var line = new Paragraph(activity) { Margin = new Thickness(0), FontSize = 12, FontStyle = FontStyles.Italic, LineHeight = 18 };
            line.SetResourceReference(TextElement.ForegroundProperty, "Assistant.QuietText");
            block.Margin = new Thickness(0, 0, 0, routine ? 4 : 10);
            block.Padding = new Thickness(ShapePadding(EntryShape.Reply).Width + (routine ? 12 : 0), 0, 0, 0);
            block.Blocks.Add(line);
            _transcriptRuns[item] = new TranscriptEntry(block, activity);
            return block;
        }

        if (item.Role is AssistantViewModel.OutputRole or AssistantViewModel.CommandRole)
        {
            // A live command's code or redacted output: monospace in a dark panel, selectable with the rest, and output is appended in place as it streams.
            var output = new Run(item.Text);
            var code = new Paragraph(output) { Margin = new Thickness(0), FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, LineHeight = 18 };
            var command = new TranscriptEntry(block, output, EntryShape.Code, AddLabel(block, item.Role, EntryShape.Code)) { Wide = true };
            block.Blocks.Add(code);
            _transcriptRuns[item] = command;
            FitBubble(command, item.Text);
            return block;
        }

        // The user's messages are a bubble on the right, replies one on the left; DwemerDistro's own notes sit between them, quieter.
        if (item.Role is not ("Codex" or AssistantViewModel.ClaudeRole or AssistantViewModel.OpenRouterRole))
        {
            var shape = item.Role == "You" ? EntryShape.User : EntryShape.Note;
            var label = AddLabel(block, item.Role, shape);
            var text = new Run(item.Text);
            var paragraph = new Paragraph(text) { Margin = new Thickness(0) };
            if (shape == EntryShape.Note)
            {
                // A note keeps its whole text, URLs and codes included, selectable and copyable with the conversation; it is only set quieter.
                paragraph.FontSize = NoteFontSize;
                paragraph.LineHeight = 19;
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "Assistant.NoteText");
            }

            block.Blocks.Add(paragraph);
            var plain = new TranscriptEntry(block, text, shape, label);
            _transcriptRuns[item] = plain;
            FitBubble(plain, item.Text);
            return block;
        }

        var entry = new TranscriptEntry(block, null, EntryShape.Reply, AddLabel(block, item.Role, EntryShape.Reply));
        _transcriptRuns[item] = entry;
        RenderMarkdown(entry, item.Text);
        FitBubble(entry, item.Text);
        return block;
    }

    /// <summary>
    /// A small role line above the shape, aligned with the text in it. It stays in the document, so a copied
    /// conversation still says who wrote what. The section's padding is the shape's padding; the backdrop paints the shape.
    /// </summary>
    private static Paragraph AddLabel(Section block, string role, EntryShape shape)
    {
        var pad = ShapePadding(shape);
        block.Padding = new Thickness(pad.Width, 0, pad.Width, pad.Height);
        var label = new Paragraph(new Run(role))
        {
            FontSize = LabelFontSize, LineHeight = 15, Margin = new Thickness(0, 0, 0, pad.Height + 4),
            TextAlignment = shape == EntryShape.User ? TextAlignment.Right : TextAlignment.Left
        };
        label.SetResourceReference(TextElement.ForegroundProperty, "Assistant.QuietText");
        block.Blocks.Add(label);
        return label;
    }

    // How far a shape reaches past its text: across (Width) and above and below (Height).
    private static System.Windows.Size ShapePadding(EntryShape shape) =>
        shape is EntryShape.User or EntryShape.Reply ? new System.Windows.Size(14, 9) : new System.Windows.Size(12, 7);

    // The width blocks are laid out in: the transcript's viewport (already inside its Padding) less the document's page padding.
    private double TranscriptWidth() =>
        Math.Max(0, TranscriptBox.ViewportWidth - TranscriptBox.Document.PagePadding.Left - TranscriptBox.Document.PagePadding.Right);

    /// <summary>
    /// Sizes a bubble through its margin only, so text, selection and scroll stay as they are. A short message gets a bubble just
    /// wide enough for it; a long one, code, a table or other Markdown structure takes the widest a bubble may be.
    /// </summary>
    private void FitBubble(TranscriptEntry entry, string text)
    {
        if (entry.Shape == EntryShape.Plain)
        {
            return;
        }

        var width = TranscriptWidth();
        var gutter = Math.Clamp(width * BubbleGutterShare, BubbleMinGutter, BubbleMaxGutter);
        var spare = gutter;
        if (width > gutter + BubbleMinWidth)
        {
            var widest = Math.Min(width - gutter, MaxBubbleWidth);
            var bubble = widest;
            var padding = 2 * ShapePadding(entry.Shape).Width;
            if (!entry.Wide)
            {
                var needed = MeasureText(entry, text, widest - padding) + padding;
                if (needed >= widest) entry.Wide = true;
                else bubble = Math.Max(needed, BubbleMinWidth);
            }

            spare = width - bubble;
        }

        var margin = entry.Shape == EntryShape.User ? new Thickness(spare, 0, 0, MessageGap) : new Thickness(0, 0, spare, MessageGap);
        if (entry.Body.Margin != margin)
        {
            entry.Body.Margin = margin;
            QueueBackdrop();
        }
    }

    private double MeasureText(TranscriptEntry entry, string text, double max)
    {
        // Monospaced live commands and output, long text and structured Markdown take the full width.
        if (entry.Shape == EntryShape.Code || text.Length > MaxMeasuredChars || entry.Shape == EntryShape.Reply && MarkdownStructureRegex().IsMatch(text))
        {
            return max;
        }

        var size = entry.Shape == EntryShape.Note ? NoteFontSize : TranscriptBox.FontSize;
        // A reply's emphasis renders a little wider than its Markdown source.
        var measured = MeasureWidth(text, size, max) * (entry.Shape == EntryShape.Reply ? 1.08 : 1) + 2;
        // The role line above must fit on one line too.
        var label = entry.Label is { } paragraph ? MeasureWidth(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text, LabelFontSize, max) + 2 : 0;
        return Math.Max(measured, label);
    }

    private double MeasureWidth(string text, double size, double max)
    {
        var typeface = new Typeface(TranscriptBox.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, typeface,
            size, System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, max) };
        return formatted.WidthIncludingTrailingWhitespace;
    }

    // At most one pass per dispatcher turn, after layout, however many tokens or changes arrived in it.
    private void QueueBackdrop()
    {
        if (_backdropQueued)
        {
            return;
        }

        _backdropQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateBackdrop);
    }

    // Something other than the newest message growing changed, so the shapes in view are measured again.
    private void RemeasureBackdrop()
    {
        _remeasure = true;
        QueueBackdrop();
    }

    /// <summary>
    /// Places a rounded shape behind each message in view. Measuring text positions after the document changed makes WPF
    /// rebuild its layout results for the whole document, so it is done only when needed: shapes are kept in document
    /// coordinates, scrolling within what was measured only moves them, and while the newest message streams its shape
    /// grows by however much the document grew. Anything else, and a short pause after streaming, measures again.
    /// </summary>
    private void UpdateBackdrop()
    {
        _backdropQueued = false;
        _shapes.Clear();
        if (TranscriptBox.Template?.FindName("PART_ContentHost", TranscriptBox) is not ScrollViewer host
            || host.Template?.FindName("PART_ScrollContentPresenter", host) is not ScrollContentPresenter presenter
            || !presenter.IsArrangeValid || !TranscriptBox.IsAncestorOf(presenter))
        {
            _measured.Clear();
            _remeasure = true;
            _backdrop.Show(Rect.Empty, _shapes);
            return;
        }

        // Character positions are in the transcript's coordinates; the visible document is its scroll presenter.
        var view = presenter.TransformToAncestor(TranscriptBox).TransformBounds(new Rect(0, 0, presenter.ActualWidth, presenter.ActualHeight));
        var scrolled = host.VerticalOffset;
        var width = TranscriptWidth();
        if (_remeasure || Math.Abs(width - _measuredWidth) >= 0.5 || scrolled < _coveredTop - 0.5 || scrolled + view.Height > _coveredBottom + 0.5)
        {
            if (!MeasureBackdrop(view, scrolled))
            {
                return;
            }

            _measuredWidth = width;
        }
        else if (host.ExtentHeight != _measuredExtent)
        {
            // Only the newest message changed: the document grew or shrank at its end, by exactly that much.
            var grown = host.ExtentHeight - _measuredExtent;
            if (_measured.Count > 0 && IsNewest(_measured[^1].Section))
            {
                _measured[^1] = _measured[^1] with { Bottom = _measured[^1].Bottom + grown };
                // Only the measured tail grew; growth below scrollback does not extend its measured coverage.
                _coveredBottom += grown;
            }
        }

        _measuredExtent = host.ExtentHeight;
        var offset = TranscriptBox.TranslatePoint(new System.Windows.Point(0, 0), _backdrop);
        var left = view.X + TranscriptBox.Document.PagePadding.Left - host.HorizontalOffset + offset.X;
        var down = view.Top - scrolled + offset.Y;
        foreach (var (section, entry, top, bottom) in _measured)
        {
            var fill = TryFindResource(entry.Shape switch
            {
                EntryShape.User => "Assistant.UserBubble",
                EntryShape.Reply => "Assistant.ReplyBubble",
                EntryShape.Code => "Assistant.CodeBubble",
                _ => "Assistant.NoteBubble"
            }) as System.Windows.Media.Brush;
            if (fill is not null && bottom > top)
            {
                // Across from the section's margins, which streaming may still change; down from the measured lines.
                var bounds = new Rect(left + section.Margin.Left, top + down, Math.Max(0, width - section.Margin.Left - section.Margin.Right), bottom - top);
                _shapes.Add(new TranscriptBackdrop.Shape(bounds, fill, entry.Shape is EntryShape.User or EntryShape.Reply ? 12 : 8));
            }
        }

        view.Offset(offset.X, offset.Y);
        _backdrop.Show(view, _shapes);
    }

    /// <summary>
    /// Measures the shapes of the messages in view from the laid-out positions of their first and last characters, in
    /// document coordinates. Only positions inside the view are measured; a message running past an edge is kept past it.
    /// </summary>
    private bool MeasureBackdrop(Rect view, double scrolled)
    {
        TextPointer? first;
        TextPointer? last;
        try
        {
            // The start of the top line in view and the end of the bottom one.
            first = TranscriptBox.GetPositionFromPoint(new System.Windows.Point(view.Left + 1, view.Top + 1), true);
            last = TranscriptBox.GetPositionFromPoint(new System.Windows.Point(view.Right - 1, view.Bottom - 1), true);
        }
        catch (InvalidOperationException)
        {
            // Layout is not current; the scroll change that follows it queues another pass.
            return false;
        }

        _measured.Clear();
        _remeasure = false;
        _coveredTop = scrolled;
        _coveredBottom = scrolled + view.Height;
        if (first is null || last is null)
        {
            return true;
        }

        // From view to document coordinates, and past the view by more than a corner so a cut shape shows no rounding there.
        var up = scrolled - view.Top;
        const double beyond = 24;
        // From the message before the first visible one, whose shape may still reach into view, to the last visible one.
        var topmost = TopBlock(first) ?? TranscriptBox.Document.Blocks.FirstBlock;
        foreach (var block in MessagesFrom(topmost?.PreviousBlock ?? topmost))
        {
            if (block is not Section { Tag: AssistantTranscriptItem item } section || !_transcriptRuns.TryGetValue(item, out var entry)
                || entry.Shape == EntryShape.Plain)
            {
                continue;
            }

            if (entry.Label?.NextBlock is not { } body)
            {
                // A reply that has not streamed any text yet shows only its role line; its first text is measured.
                if (section.ContentStart.GetInsertionPosition(LogicalDirection.Forward).CompareTo(last) <= 0) _remeasure = true;
                continue;
            }

            var start = body.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            if (start.CompareTo(last) > 0)
            {
                break;
            }

            // A message ending just above the view is still measured: its shape's lower padding may reach into it.
            var stop = section.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            var pad = ShapePadding(entry.Shape);
            var top = start.CompareTo(first) < 0 ? view.Top - beyond : start.GetCharacterRect(LogicalDirection.Forward).Top - pad.Height;
            var exact = stop.CompareTo(last) <= 0;
            var bottom = exact ? stop.GetCharacterRect(LogicalDirection.Backward).Bottom + pad.Height : view.Bottom + beyond;
            if (double.IsFinite(top) && double.IsFinite(bottom) && bottom > top)
            {
                _measured.Add((section, entry, top + up, bottom + up));
                // Everything after the newest message's measured end is that message, so its growth stays covered.
                if (exact && IsNewest(section)) _coveredBottom = double.PositiveInfinity;
            }
        }

        return true;
    }

    // The top-level block a position is in, a message or an Activity group, e.g. the one at the top of the view.
    private Block? TopBlock(TextPointer position)
    {
        for (var node = position.GetInsertionPosition(LogicalDirection.Forward).Parent as TextElement; node is not null; node = node.Parent as TextElement)
        {
            if (node is Block block && ReferenceEquals(block.Parent, TranscriptBox.Document))
            {
                return block;
            }
        }

        return null;
    }

    // Each message from a top-level block on, with the items of an opened Activity group in its place; a folded group shows none.
    private static IEnumerable<Block> MessagesFrom(Block? block)
    {
        for (; block is not null; block = block.NextBlock)
        {
            if (block is not Section { Tag: ActivityGroup group })
            {
                yield return block;
            }
            else if (group.IsExpanded)
            {
                foreach (var item in group.Items.Blocks)
                {
                    yield return item;
                }
            }
        }
    }

    // The document's last message, also as the last item of an opened Activity group at its end; never a folded group's item.
    private bool IsNewest(Block block)
    {
        while (block.NextBlock is null && block.Parent is Block parent)
        {
            block = parent;
        }

        return block.NextBlock is null && ReferenceEquals(block.Parent, TranscriptBox.Document);
    }

    // Code fences, tables, headings, lists and quotes render wider than their source, so such replies are not measured.
    [GeneratedRegex(@"```|\||^\s*(#|>|[-*+]\s|\d+[.)]\s)", RegexOptions.Multiline)]
    private static partial Regex MarkdownStructureRegex();

    // Bubbles follow the transcript's width, including when its scroll bar appears or goes; their shapes follow every scroll.
    private void TranscriptBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        QueueBackdrop();
        var width = TranscriptWidth();
        if (Math.Abs(width - _bubbleWidth) < 0.5)
        {
            return;
        }

        var follow = IsFollowingTranscript();
        _bubbleWidth = width;
        RemeasureBackdrop();
        foreach (var (item, entry) in _transcriptRuns)
        {
            entry.Wide = false;
            FitBubble(entry, item.Text);
        }

        if (follow)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => TranscriptBox.ScrollToEnd());
        }
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
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            var transcript = _viewModel!.Transcript;
            var start = e.NewStartingIndex >= 0 ? e.NewStartingIndex : transcript.Count - e.NewItems.Count;
            for (var offset = 0; offset < e.NewItems.Count; offset++)
            {
                if (!PlaceTranscriptBlock(transcript, start + offset))
                {
                    RebuildTranscript();
                    return;
                }
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            foreach (AssistantTranscriptItem item in e.OldItems)
            {
                item.PropertyChanged -= TranscriptItem_PropertyChanged;
                if (!_transcriptRuns.Remove(item, out var entry))
                {
                    continue;
                }

                entry.Body.SiblingBlocks?.Remove(entry.Body);
                if (entry.Group is { } group)
                {
                    // An emptied group goes with its last item.
                    if (group.Items.Blocks.Count == 0) group.Section.SiblingBlocks?.Remove(group.Section);
                    else group.Recount();
                }
            }
        }
        else
        {
            RebuildTranscript();
            return;
        }

        RestoreSelection(pinned);
        RemeasureBackdrop();
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

        // An item in a folded group changes only its own detached text; nothing on screen moves.
        var hidden = entry.Group is { IsExpanded: false };
        var follow = !hidden && IsFollowingTranscript();
        var pinned = hidden ? null : PinSelection();
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
            entry.Wide = false;
        }

        FitBubble(entry, item.Text);
        if (hidden)
        {
            return;
        }

        if (IsNewest(entry.Body))
        {
            // The newest message streaming: its shape follows the document's growth now and is measured once it pauses.
            QueueBackdrop();
            _settle.Stop();
            _settle.Start();
        }
        else
        {
            RemeasureBackdrop();
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

    // Shown again, e.g. back from API Keys: an OpenRouter chat without a usable key reads its key once more.
    private void AssistantView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            RemeasureBackdrop();
            _viewModel?.OnPageShown();
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
            // A text-only model is offered text files only; an image chosen anyway is refused with a note under the composer.
            Filter = (viewModel.CanAttachImages ? "Screenshots and text files|*.png;*.jpg;*.jpeg;*.gif;*.bmp;" : "Text files|")
                     + "*.txt;*.log;*.md;*.json;*.yaml;*.yml;*.toml;*.ini;*.cfg;*.conf;*.xml;*.csv;*.py;*.php;*.js;*.cs;*.sh;*.ps1;*.sql;*.lua;*.psc|All files|*.*"
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
