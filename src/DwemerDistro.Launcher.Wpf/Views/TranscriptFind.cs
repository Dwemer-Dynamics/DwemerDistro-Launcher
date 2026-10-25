using System.Text;
using System.Windows.Documents;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>
/// Exact text search in the transcript document as shown: ordinal, case-sensitive Unicode, across the runs a rendered
/// reply is split into, never across paragraphs. Only what the document holds is searched, so the count covers the
/// loaded window of the conversation, not the whole chat.
/// </summary>
internal static class TranscriptFind
{
    internal const int MaxMatches = 1000;

    internal static List<TextRange> Matches(FlowDocument document, string query)
    {
        var results = new List<TextRange>();
        if (string.IsNullOrEmpty(query) || query.Contains('\n')) return results;
        var text = new StringBuilder();
        var segments = new List<(int Start, int Length, TextPointer Pointer)>();
        for (var pointer = document.ContentStart; pointer is not null && pointer.CompareTo(document.ContentEnd) < 0;
             pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (pointer.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var run = pointer.GetTextInRun(LogicalDirection.Forward);
                    segments.Add((text.Length, run.Length, pointer));
                    text.Append(run);
                    break;
                // A paragraph, line break or cell boundary separates text, so no match spans it.
                case TextPointerContext.ElementStart when pointer.GetAdjacentElement(LogicalDirection.Forward) is Paragraph or LineBreak or TableCell or ListItem:
                    text.Append('\n');
                    break;
            }
        }

        var all = text.ToString();
        for (var index = all.IndexOf(query, StringComparison.Ordinal); index >= 0 && results.Count < MaxMatches;
             index = all.IndexOf(query, index + query.Length, StringComparison.Ordinal))
        {
            if (Locate(segments, index, end: false) is { } start && Locate(segments, index + query.Length, end: true) is { } finish)
                results.Add(new TextRange(start, finish));
        }
        return results;
    }

    // A character offset in the searched text as a position in the document; an end may sit just after a run's last character.
    private static TextPointer? Locate(List<(int Start, int Length, TextPointer Pointer)> segments, int offset, bool end)
    {
        foreach (var (start, length, pointer) in segments)
        {
            if (offset >= start && (end ? offset <= start + length : offset < start + length))
                return pointer.GetPositionAtOffset(offset - start, LogicalDirection.Forward);
        }
        return null;
    }
}
