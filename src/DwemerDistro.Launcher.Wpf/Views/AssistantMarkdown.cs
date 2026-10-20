using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>
/// A bounded Markdown subset rendered into FlowDocument blocks: ATX headings, paragraphs, emphasis, inline code, fenced
/// code blocks, flat bulleted and numbered lists, pipe tables and http(s) links. Everything else, raw HTML included, is
/// shown as literal text. Links are never made clickable here (the transcript is not document-enabled), so nothing
/// opens on its own; the address is shown as a tooltip.
/// </summary>
internal static partial class AssistantMarkdown
{
    internal const int MaxChunks = 600;
    internal const int MaxTableColumns = 12;
    internal const int MaxTableRows = 200;
    private const int MaxInlineDepth = 4;

    /// <summary>
    /// Splits text into top-level chunks: one fenced code block, or a run of lines up to a blank line. Rendering per chunk
    /// keeps older chunks stable while a reply streams; only chunks from the first one that changed are rendered again.
    /// An unclosed fence runs to the end, so a streaming code block grows as one chunk.
    /// </summary>
    internal static List<string> SplitChunks(string text)
    {
        var chunks = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var current = new StringBuilder();
        string? fence = null;
        void Flush()
        {
            if (current.Length > 0) chunks.Add(current.ToString().TrimEnd('\n'));
            current.Clear();
        }
        foreach (var line in lines)
        {
            if (chunks.Count >= MaxChunks - 1)
            {
                current.Append(line).Append('\n');
                continue;
            }
            var trimmed = line.TrimStart();
            if (fence is not null)
            {
                current.Append(line).Append('\n');
                if (trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.Trim() == fence)
                {
                    fence = null;
                    Flush();
                }
                continue;
            }
            if (FenceRegex().Match(trimmed) is { Success: true } open)
            {
                Flush();
                fence = open.Groups[1].Value;
                current.Append(line).Append('\n');
                continue;
            }
            if (line.Trim().Length == 0)
            {
                Flush();
                continue;
            }
            // A heading stands alone even without blank lines around it.
            if (HeadingRegex().IsMatch(line))
            {
                Flush();
                chunks.Add(line);
                continue;
            }
            current.Append(line).Append('\n');
        }
        Flush();
        return chunks;
    }

    internal static Block[] RenderChunk(string chunk)
    {
        var lines = chunk.Split('\n');
        if (FenceRegex().Match(lines[0].TrimStart()) is { Success: true } fence)
        {
            var body = lines.Skip(1).ToList();
            if (body.Count > 0 && body[^1].Trim() == fence.Groups[1].Value) body.RemoveAt(body.Count - 1);
            return [CodeBlock(body, fence.Groups[2].Value.Trim())];
        }
        if (lines.Length == 1 && HeadingRegex().Match(lines[0]) is { Success: true } heading)
        {
            var level = heading.Groups[1].Value.Length;
            var paragraph = new Paragraph
            {
                FontWeight = FontWeights.SemiBold, FontSize = level switch { 1 => 18, 2 => 16, 3 => 14.5, _ => 13.5 },
                Margin = new Thickness(0, 6, 0, 2)
            };
            AddInlines(paragraph.Inlines, heading.Groups[2].Value.TrimEnd('#', ' '), 0);
            return [paragraph];
        }
        if (lines.Length >= 2 && IsTableRow(lines[0]) && TableSeparatorRegex().IsMatch(lines[1].Trim()) && lines.Skip(2).All(IsTableRow))
            return [Table(lines)];
        if (lines.All(line => ListItemRegex().IsMatch(line) || line.StartsWith("  ", StringComparison.Ordinal))
            && ListItemRegex().IsMatch(lines[0]))
            return [ListBlock(lines)];

        var text = new Paragraph { Margin = new Thickness(0, 2, 0, 4) };
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) text.Inlines.Add(new LineBreak());
            var line = lines[index];
            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                line = line[2..];
                text.BorderBrush = System.Windows.Media.Brushes.Gray;
                text.BorderThickness = new Thickness(2, 0, 0, 0);
                text.Padding = new Thickness(8, 0, 0, 0);
            }
            AddInlines(text.Inlines, line, 0);
        }
        return [text];
    }

    private static bool IsTableRow(string line) => line.Trim() is { Length: > 1 } trimmed && trimmed[0] == '|';

    private static Paragraph CodeBlock(IReadOnlyList<string> lines, string language)
    {
        // One paragraph with line breaks, so a selection copies the code exactly, indentation included.
        var code = new Paragraph
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0, 0, 0)),
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 4, 0, 6)
        };
        if (language.Length > 0) code.ToolTip = language;
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0) code.Inlines.Add(new LineBreak());
            code.Inlines.Add(new Run(lines[index]));
        }
        return code;
    }

    private static List ListBlock(string[] lines)
    {
        var ordered = char.IsAsciiDigit(lines[0].TrimStart()[0]);
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(18, 0, 0, 0)
        };
        if (ordered && int.TryParse(ListItemRegex().Match(lines[0]).Groups[1].Value.TrimEnd('.', ')'), out var start) && start is > 0 and < 100000)
            list.StartIndex = start;
        Paragraph? current = null;
        foreach (var line in lines)
        {
            if (ListItemRegex().Match(line) is { Success: true } item)
            {
                current = new Paragraph { Margin = new Thickness(0) };
                AddInlines(current.Inlines, item.Groups[2].Value, 0);
                list.ListItems.Add(new ListItem(current));
            }
            else if (current is not null)
            {
                // A continuation line stays in its item; nesting is shown flat.
                current.Inlines.Add(new LineBreak());
                AddInlines(current.Inlines, line.Trim(), 0);
            }
        }
        return list;
    }

    private static Table Table(string[] lines)
    {
        static string[] Cells(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
            if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
            return trimmed.Split('|').Select(cell => cell.Trim()).Take(MaxTableColumns).ToArray();
        }
        var header = Cells(lines[0]);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
        table.BorderBrush = System.Windows.Media.Brushes.Gray;
        for (var column = 0; column < header.Length; column++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (var (line, index) in lines.Where((_, i) => i != 1).Take(MaxTableRows + 1).Select((line, i) => (line, i)))
        {
            var row = new TableRow();
            var cells = Cells(line);
            for (var column = 0; column < header.Length; column++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                if (index == 0) paragraph.FontWeight = FontWeights.SemiBold;
                AddInlines(paragraph.Inlines, column < cells.Length ? cells[column] : string.Empty, 0);
                row.Cells.Add(new TableCell(paragraph)
                {
                    Padding = new Thickness(4, 2, 8, 2), BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(0, 0, 0, index == 0 ? 1 : 0)
                });
            }
            group.Rows.Add(row);
        }
        if (lines.Length - 1 > MaxTableRows + 1)
        {
            var more = new TableRow();
            more.Cells.Add(new TableCell(new Paragraph(new Run($"… {lines.Length - 2 - MaxTableRows} more row(s) not shown as a table."))) { ColumnSpan = header.Length });
            group.Rows.Add(more);
        }
        return table;
    }

    /// <summary>Inline code, bold, italic and safe links; anything unmatched stays literal, so HTML is shown as typed.</summary>
    internal static void AddInlines(InlineCollection inlines, string text, int depth)
    {
        var position = 0;
        foreach (Match match in InlineRegex().Matches(text))
        {
            if (match.Index < position) continue;
            if (match.Index > position) inlines.Add(new Run(text[position..match.Index]));
            position = match.Index + match.Length;
            if (match.Groups["code"].Success)
            {
                inlines.Add(new Run(match.Groups["code"].Value) { FontFamily = new System.Windows.Media.FontFamily("Consolas") });
            }
            else if (match.Groups["link"].Success)
            {
                var label = match.Groups["label"].Value;
                if (IsSafeLink(match.Groups["url"].Value, out var uri))
                {
                    // Not document-enabled, so the link is styled and its address shown, but nothing navigates on click.
                    var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
                    link.Inlines.Add(new Run(label));
                    inlines.Add(link);
                }
                else
                {
                    inlines.Add(new Run(match.Value));
                }
            }
            else if (depth < MaxInlineDepth)
            {
                Span span = match.Groups["bold"].Success ? new Bold() : new Italic();
                AddInlines(span.Inlines, match.Groups["bold"].Success ? match.Groups["bold"].Value
                    : match.Groups["em"].Success ? match.Groups["em"].Value : match.Groups["em2"].Value, depth + 1);
                inlines.Add(span);
            }
            else
            {
                inlines.Add(new Run(match.Value));
            }
        }
        if (position < text.Length) inlines.Add(new Run(text[position..]));
    }

    /// <summary>Absolute http and https addresses only; javascript:, file:, data: and relative links stay literal text.</summary>
    internal static bool IsSafeLink(string url, out Uri uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https" && url.Length <= 2048;

    [GeneratedRegex(@"^(`{3,}|~{3,})(.*)$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^\s{0,3}([-*+]|\d{1,6}[.)])\s+(.*)$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"`(?<code>[^`]+)`|(?<link>\[(?<label>[^\]]{1,500})\]\((?<url>[^)\s]{1,2048})\))|\*\*(?<bold>[^*]+?)\*\*|(?<![\w*])\*(?<em>[^*\s][^*]*?)\*(?![\w*])|(?<!\w)_(?<em2>[^_\s][^_]*?)_(?!\w)")]
    private static partial Regex InlineRegex();
}
