using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// DwemerDistro's shared component catalog: each component's identity and the path rules that tell it is installed.
/// The canonical file is Core's etc/dwemerdistro-components.json (installed as /etc/dwemerdistro-components.json); the
/// launcher embeds a byte-identical copy so it builds alone and can probe offline. It is data validated against Core's
/// grammar (bin/ddistro catalog_parse): a closed set of path tests, never a command, expression, port, log or action.
/// </summary>
public sealed partial class ComponentCatalog
{
    public const int SchemaVersion = 1;
    public const int MaxBytes = 64 * 1024;
    public const int MaxEntries = 48;
    public const int MaxStartup = 32;
    public const int MaxAliases = 4;
    public const int MaxLeaves = 4;
    public const int MaxSegments = 10;
    public const int MaxLabelChars = 64;
    internal const string ResourceName = "DwemerDistro.Launcher.Wpf.Resources.dwemerdistro-components.json";
    public static readonly IReadOnlyList<string> Roles = ["llm", "embeddings", "tts", "stt", "mcp", "dashboard", "gpu_runtime"];
    private static readonly string[] Roots = ["/home/dwemer/", "/var/www/html/", "/usr/"];
    private static readonly string[] LeafKinds = ["exists", "file", "dir", "glob", "which"];
    // which: a program name from this closed set; Core looks it up in a fixed PATH and never runs it.
    private static readonly string[] WhichPrograms = ["nvcc"];
    private static readonly HashSet<string> DocumentKeys = ["schema_version", "catalog_version", "components"];
    private static readonly HashSet<string> EntryKeys = ["id", "aliases", "name", "role", "listed", "installed", "startup"];
    private static readonly Lazy<ComponentCatalog> EmbeddedCatalog = new(LoadEmbedded);
    private readonly Dictionary<string, ComponentCatalogEntry> _byName;

    private ComponentCatalog(string catalogVersion, string sha256, IReadOnlyList<ComponentCatalogEntry> entries)
    {
        CatalogVersion = catalogVersion;
        Sha256 = sha256;
        Entries = entries;
        _byName = new Dictionary<string, ComponentCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var name in entry.Aliases.Prepend(entry.Id))
            {
                _byName[name] = entry;
            }
        }
    }

    public string CatalogVersion { get; }

    /// <summary>Lowercase hex SHA-256 of the exact file bytes, as Core reports it.</summary>
    public string Sha256 { get; }

    public IReadOnlyList<ComponentCatalogEntry> Entries { get; }

    /// <summary>The catalog built into this launcher, parsed once. A broken embedded copy is a build defect, so it throws.</summary>
    public static ComponentCatalog Embedded => EmbeddedCatalog.Value;

    /// <summary>The entry for a catalog id or an older Core id listed as its alias; null when unknown.</summary>
    public ComponentCatalogEntry? Find(string idOrAlias) =>
        _byName.TryGetValue(idOrAlias ?? string.Empty, out var entry) ? entry : null;

    internal static byte[] ReadEmbeddedBytes()
    {
        using var stream = typeof(ComponentCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The embedded component catalog is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static ComponentCatalog LoadEmbedded() => Parse(ReadEmbeddedBytes());

    public static bool TryParse(byte[] raw, out ComponentCatalog? catalog, out string error)
    {
        try
        {
            catalog = Parse(raw);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException ex)
        {
            catalog = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The catalog in the raw file bytes; InvalidDataException for anything outside Core's contract.</summary>
    public static ComponentCatalog Parse(byte[] raw)
    {
        if (raw.Length > MaxBytes) throw Invalid("size");
        // Python's utf-8 decode keeps a byte order mark, which json.loads then refuses.
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) throw Invalid("document");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw Invalid("document");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Keys(root).SetEquals(DocumentKeys)) throw Invalid("document");
            var schema = root.GetProperty("schema_version");
            var version = root.GetProperty("catalog_version");
            var components = root.GetProperty("components");
            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != SchemaVersion.ToString(CultureInfo.InvariantCulture)
                || version.ValueKind != JsonValueKind.String || !VersionRegex().IsMatch(version.GetString()!)
                || components.ValueKind != JsonValueKind.Array || components.GetArrayLength() is < 1 or > MaxEntries)
            {
                throw Invalid("document");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<ComponentCatalogEntry>();
            foreach (var item in components.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Invalid("entry keys");
                var keys = Keys(item);
                if (!keys.IsSubsetOf(EntryKeys) || !EntryKeys.Where(key => key != "startup").All(keys.Contains)) throw Invalid("entry keys");
                var aliases = item.GetProperty("aliases");
                if (aliases.ValueKind != JsonValueKind.Array || aliases.GetArrayLength() > MaxAliases) throw Invalid("aliases");
                var names = new List<string>();
                foreach (var name in aliases.EnumerateArray().Prepend(item.GetProperty("id")))
                {
                    if (name.ValueKind != JsonValueKind.String || !IsId(name.GetString()) || !seen.Add(name.GetString()!)) throw Invalid("id");
                    names.Add(name.GetString()!);
                }

                var label = item.GetProperty("name");
                var role = item.GetProperty("role");
                var listed = item.GetProperty("listed");
                if (label.ValueKind != JsonValueKind.String || !IsLabel(label.GetString())
                    || role.ValueKind != JsonValueKind.String || !Roles.Contains(role.GetString())
                    || listed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw Invalid("entry");
                }

                entries.Add(new ComponentCatalogEntry(
                    names[0],
                    names.Skip(1).ToArray(),
                    label.GetString()!,
                    role.GetString()!,
                    listed.GetBoolean(),
                    ParsePredicate(item.GetProperty("installed"), startup: false),
                    item.TryGetProperty("startup", out var startup) ? ParsePredicate(startup, startup: true) : null));
            }

            if (entries.Count(entry => entry.Startup is not null) > MaxStartup) throw Invalid("startup");
            return new ComponentCatalog(version.GetString()!, Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), entries);
        }
    }

    /// <summary>A leaf, or all/any over 1..MaxLeaves leaves (no nesting); startup rules use exists leaves only.</summary>
    private static ComponentPredicate ParsePredicate(JsonElement value, bool startup)
    {
        if (value.ValueKind != JsonValueKind.Object || Keys(value).Count != 1) throw Invalid("predicate");
        var property = value.EnumerateObject().Single();
        var group = property.Name is "all" or "any";
        if (group && property.Value.ValueKind != JsonValueKind.Array) throw Invalid("predicate");
        var leaves = group ? property.Value.EnumerateArray().ToArray() : [value];
        if (leaves.Length is < 1 or > MaxLeaves) throw Invalid("predicate");
        var parsed = new List<ComponentLeaf>();
        foreach (var leaf in leaves)
        {
            if (leaf.ValueKind != JsonValueKind.Object || Keys(leaf).Count != 1) throw Invalid("leaf");
            var test = leaf.EnumerateObject().Single();
            if (!LeafKinds.Contains(test.Name) || startup && test.Name != "exists" || test.Value.ValueKind != JsonValueKind.String) throw Invalid("leaf");
            var argument = test.Value.GetString()!;
            if (!(test.Name == "which" ? WhichPrograms.Contains(argument) : IsCatalogPath(argument, test.Name == "glob"))) throw Invalid("leaf");
            parsed.Add(new ComponentLeaf(test.Name, argument));
        }

        return new ComponentPredicate(property.Name == "any" ? "any" : "all", parsed);
    }

    /// <summary>Property names, refusing a duplicate (System.Text.Json would otherwise keep the last one).</summary>
    private static HashSet<string> Keys(JsonElement element)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!keys.Add(property.Name)) throw Invalid("duplicate key");
        }

        return keys;
    }

    internal static bool IsId(string? value) => value is not null && IdRegex().IsMatch(value);

    /// <summary>
    /// A display label only, never a path or token: 1..64 printable characters (spaces, parentheses and slashes allowed),
    /// trimmed, and unchanged by Core's secret redaction.
    /// </summary>
    internal static bool IsLabel(string? value)
    {
        if (value is null || value.Length == 0 || value != value.Trim() || value.EnumerateRunes().Count() > MaxLabelChars) return false;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value != ' ' && category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator)
            {
                return false;
            }
        }

        return !RedactedShapeRegex().IsMatch(value);
    }

    /// <summary>A normalized absolute path below an allowed root; a glob path has exactly one '*' after a literal prefix in one name.</summary>
    internal static bool IsCatalogPath(string value, bool glob)
    {
        if (value.Length > 255 || !Roots.Any(root => value.StartsWith(root, StringComparison.Ordinal)) || value.EndsWith('/')
            || value.Contains("//", StringComparison.Ordinal) || value.Contains('\\') || value.Count(c => c == '*') != (glob ? 1 : 0))
        {
            return false;
        }

        var parts = value[1..].Split('/');
        if (parts.Length > MaxSegments) return false;
        foreach (var part in parts)
        {
            var star = part.IndexOf('*');
            var prefix = star < 0 ? part : part[..star];
            var suffix = star < 0 ? string.Empty : part[(star + 1)..];
            if (part is "." or ".." || !SegmentRegex().IsMatch(prefix) || prefix.StartsWith('.') && star >= 0) return false;
            if (suffix.Length > 0 && !SegmentRegex().IsMatch(suffix)) return false;
        }

        return true;
    }

    private static InvalidDataException Invalid(string what) => new("component catalog: " + what);

    [GeneratedRegex(@"^[a-z][a-z0-9_]{1,31}\z")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]{0,31}\z")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._+-]{1,64}\z")]
    private static partial Regex SegmentRegex();

    // What Core's redact() would rewrite: URL credentials or queries, auth schemes, key=value secrets, and token shapes.
    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.-]{1,15}://[^\s/@""'<>]+@|\bhttps?://[^\s""'<>?#]+[?#]|\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}|(authorization|api[_-]?key|apikey|pairing[_-]?key|access[_-]?token|refresh[_-]?token|auth[_-]?token|token|password|passwd|pwd|secret|private[_-]?key)[""']?\s*[:=]|\b(hf_|sk-|gh[pousr]_|github_pat_|xox[abp]-)[A-Za-z0-9_-]{16,}")]
    private static partial Regex RedactedShapeRegex();
}

public sealed record ComponentCatalogEntry(
    string Id,
    IReadOnlyList<string> Aliases,
    string Name,
    string Role,
    bool Listed,
    ComponentPredicate Installed,
    ComponentPredicate? Startup);

/// <summary>"all" or "any" over one to four leaves.</summary>
public sealed record ComponentPredicate(string Mode, IReadOnlyList<ComponentLeaf> Leaves);

/// <summary>One test: exists, file, dir or glob on a validated path, or which on an allowlisted program name.</summary>
public sealed record ComponentLeaf(string Kind, string Argument);
