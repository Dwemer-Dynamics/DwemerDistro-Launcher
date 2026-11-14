using System.Text.Json;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <param name="Kind"><c>live</c> for CHIM's current request log, <c>save</c> for one held by a Playthrough Save.</param>
/// <param name="Saves">The names of the saves that share the schema; empty for the live log.</param>
internal sealed record ChimRequestLogTable(string Schema, string Kind, IReadOnlyList<string> Saves, long Bytes);

internal sealed record ChimRequestLogSkipped(string Schema, string Reason, long Bytes);

/// <param name="Token">Pins the exact set of tables the user agreed to clear; Core refuses the clear if it changed.</param>
internal sealed record ChimRequestLogPreview(
    string Token, long TotalBytes, IReadOnlyList<ChimRequestLogTable> Tables, IReadOnlyList<ChimRequestLogSkipped> Skipped);

/// <param name="ReclaimedBytes">What the emptied tables released inside PostgreSQL.</param>
/// <param name="FilesystemFreedBytes">How much free space the distro filesystem gained; the WSL virtual disk keeps its size.</param>
internal sealed record ChimRequestLogClearResult(int ClearedTables, long ReclaimedBytes, long FilesystemFreedBytes);

/// <summary>
/// Clearing CHIM's request logs through Core's <c>request-logs-preview</c> and <c>request-logs-clear</c>. Core owns
/// which tables qualify (live data and verified saves only), the locks and the single TRUNCATE transaction; the
/// launcher only shows the preview, asks, and passes back the preview's token.
/// </summary>
internal static class ChimRequestLogMaintenance
{
    internal const string PreviewVerb = "request-logs-preview";
    internal const string ClearVerb = "request-logs-clear";

    internal const string ToolTooOldMessage =
        "This distro's database maintenance tool cannot clear CHIM request logs yet. Run Update Distro in " +
        "Settings > First-Time Setup to install the current tool, then try again. Nothing was changed.";

    internal static string[] BuildPreviewArguments() => [Pg17UpdateMigration.ToolPath, PreviewVerb];

    internal static string[] BuildClearArguments(string token) => IsToken(token)
        ? [Pg17UpdateMigration.ToolPath, ClearVerb, "--confirm-preview", token]
        : throw new ArgumentException("The request log preview token is malformed.", nameof(token));

    /// <summary>Core's token: exactly 64 lowercase hexadecimal characters.</summary>
    internal static bool IsToken(string? token) =>
        token is { Length: 64 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>An older Core rejects the unknown action as a usage error before touching anything.</summary>
    internal static bool IsUnsupportedByTool(CommandResult result) =>
        result.ExitCode == 2
        && (result.StandardError.Contains("invalid choice", StringComparison.Ordinal)
            || result.StandardOutput.Contains("invalid choice", StringComparison.Ordinal));

    /// <summary>Null unless the last JSON line is a complete preview with a valid token.</summary>
    internal static ChimRequestLogPreview? ParsePreview(string? output)
    {
        if (LastJsonObject(output) is not { } root) return null;
        try
        {
            var token = root.GetProperty("token").GetString();
            if (!IsToken(token)) return null;
            var tables = new List<ChimRequestLogTable>();
            foreach (var table in root.GetProperty("tables").EnumerateArray())
            {
                var saves = table.GetProperty("saves").EnumerateArray().Select(save => save.GetString() ?? string.Empty).ToList();
                tables.Add(new(table.GetProperty("schema").GetString() ?? string.Empty,
                    table.GetProperty("kind").GetString() ?? string.Empty, saves, table.GetProperty("bytes").GetInt64()));
            }

            var skipped = root.GetProperty("skipped").EnumerateArray()
                .Select(item => new ChimRequestLogSkipped(item.GetProperty("schema").GetString() ?? string.Empty,
                    item.GetProperty("reason").GetString() ?? string.Empty, item.GetProperty("bytes").GetInt64()))
                .ToList();
            var total = root.GetProperty("total_bytes").GetInt64();
            return total < 0 || tables.Any(table => table.Bytes < 0) ? null : new(token!, total, tables, skipped);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    internal static ChimRequestLogClearResult? ParseClear(string? output)
    {
        if (LastJsonObject(output) is not { } root) return null;
        try
        {
            return new(root.GetProperty("cleared_tables").GetInt32(), root.GetProperty("reclaimed_bytes").GetInt64(),
                root.GetProperty("filesystem_freed_bytes").GetInt64());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static JsonElement? LastJsonObject(string? output)
    {
        var line = (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(text => text.StartsWith('{'));
        if (line is null) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
