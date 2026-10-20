using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>Bounds of one export; past any of them the file says it is partial and why.</summary>
internal sealed record AssistantExportLimits(int MaxTurnPages = 5000, int MaxItemPagesPerTurn = 200, int MaxBytes = 64 * 1024 * 1024);

/// <summary>A built export: the Markdown, how many messages it holds, and whether it is the whole conversation.</summary>
internal sealed record AssistantExportResult(string Markdown, int Messages, bool Complete, string Status);

/// <summary>
/// Builds a Markdown export of one chat from the App Server's own pages, not from the transcript's 200-message window.
/// Read-only: it lists turns and items and nothing else, so the thread, the saved references and the model are untouched.
/// </summary>
internal static class AssistantChatExport
{
    // Core's broker accepts at most 10 turns per thread/turns/list page.
    internal const int TurnPageSize = 10;
    internal const int ItemPageSize = 50;

    /// <summary>
    /// Reads every thread oldest first (earlier threads of the task, then the current one) through rust-v0.163.0
    /// <c>thread/turns/list</c> in ascending order with full items. A turn whose items are a summary or not loaded is read
    /// through <c>thread/items/list</c>. Only the user's own text and the assistant's replies are written, exactly as
    /// stored: the launcher's hidden context, tool calls and results, commands and reasoning are left out. A repeated
    /// cursor, an error or a limit stops that part and marks the export partial; cancellation throws and writes nothing.
    /// </summary>
    internal static async Task<AssistantExportResult> BuildAsync(
        Func<string, JsonObject, CancellationToken, Task<JsonElement>> request, string title, IReadOnlyList<string> threadIds,
        AssistantExportLimits limits, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        var problems = new List<string>();
        var messages = 0;
        var omitted = 0;
        var bytes = 0;
        var full = false;
        for (var index = 0; index < threadIds.Count && !full; index++)
        {
            var threadId = threadIds[index];
            var earlier = index < threadIds.Count - 1;
            if (threadIds.Count > 1)
                Append(earlier ? $"## Earlier thread {index + 1} (before this chat moved to a new thread)\n\n" : "## Current thread\n\n");
            var turnCursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            try
            {
                for (var page = 0; !full; page++)
                {
                    if (page == limits.MaxTurnPages)
                    {
                        problems.Add($"stopped after {limits.MaxTurnPages:N0} pages of turns");
                        break;
                    }
                    var turnsRequest = new JsonObject { ["threadId"] = threadId, ["limit"] = TurnPageSize, ["sortDirection"] = "asc", ["itemsView"] = "full" };
                    if (cursor is not null) turnsRequest["cursor"] = cursor;
                    var result = await request("thread/turns/list", turnsRequest, cancellationToken);
                    foreach (var turn in Array(result, "data"))
                    {
                        var items = new List<JsonElement>();
                        if (String(turn, "itemsView") == "full") items.AddRange(Array(turn, "items"));
                        else await ReadTurnItemsAsync(request, threadId, String(turn, "id"), items, limits, problems, cancellationToken);
                        foreach (var (role, text, _) in AssistantViewModel.ReadMessages(items, ref omitted))
                        {
                            if (role is not ("You" or "Codex")) continue;
                            var entry = (role == "You" ? "### You\n\n" : "### Assistant\n\n") + text + "\n\n";
                            if (bytes + Encoding.UTF8.GetByteCount(entry) > limits.MaxBytes)
                            {
                                problems.Add($"stopped at the {limits.MaxBytes / (1024 * 1024.0):0.#} MiB size limit");
                                full = true;
                                break;
                            }
                            Append(entry);
                            messages++;
                        }
                        if (full) break;
                    }
                    cursor = String(result, "nextCursor") is { Length: > 0 } next ? next : null;
                    if (cursor is null) break;
                    if (!turnCursors.Add(cursor))
                    {
                        problems.Add("the conversation's page cursor repeated, so reading stopped there");
                        break;
                    }
                }
            }
            catch (AppServerException ex)
            {
                problems.Add((earlier ? "an earlier thread" : "the conversation") + " could not be read further: " + Shorten(ex.Message, 160));
            }
        }
        if (omitted > 0) problems.Add($"{omitted} message(s) could not be exported safely and were left out");

        var complete = problems.Count == 0;
        var status = complete
            ? $"Exported the whole chat: {messages:N0} message(s)."
            : $"PARTIAL export: {messages:N0} message(s); " + string.Join("; ", problems) + ".";
        var header = new StringBuilder()
            .Append("# ").Append(title).Append("\n\n")
            .Append($"Exported from the DwemerDistro Assistant on {now.UtcDateTime:yyyy-MM-dd HH:mm} UTC. ")
            .Append(complete ? "This is the whole conversation." : status).Append("\n\n")
            .Append("Only your messages and the assistant's replies are included, exactly as stored. The launcher's hidden context, ")
            .Append("tool calls and results, commands and reasoning are left out.\n\n---\n\n");
        return new AssistantExportResult(header.Append(body).ToString(), messages, complete, status);

        void Append(string text)
        {
            body.Append(text);
            bytes += Encoding.UTF8.GetByteCount(text);
        }
    }

    /// <summary>One turn's items through <c>thread/items/list</c>; anything left unread is added to the problems.</summary>
    private static async Task ReadTurnItemsAsync(Func<string, JsonObject, CancellationToken, Task<JsonElement>> request, string threadId,
        string turnId, List<JsonElement> items, AssistantExportLimits limits, List<string> problems, CancellationToken cancellationToken)
    {
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < limits.MaxItemPagesPerTurn; page++)
        {
            var itemsRequest = new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId, ["limit"] = ItemPageSize };
            if (cursor is not null) itemsRequest["cursor"] = cursor;
            var listed = await request("thread/items/list", itemsRequest, cancellationToken);
            items.AddRange(Array(listed, "data").Select(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("item", out var item) ? item : default));
            cursor = String(listed, "nextCursor") is { Length: > 0 } next ? next : null;
            if (cursor is null) return;
            if (!cursors.Add(cursor))
            {
                problems.Add("a turn's item cursor repeated, so the rest of that turn was left out");
                return;
            }
        }
        problems.Add($"a turn had more than {limits.MaxItemPagesPerTurn * ItemPageSize:N0} items; the rest were left out");
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList() : [];

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static string Shorten(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";
}
