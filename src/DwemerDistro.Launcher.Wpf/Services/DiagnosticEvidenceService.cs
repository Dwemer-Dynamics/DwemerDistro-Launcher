using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

// Bounded, read-only primitives shared by the new support-report sections and their tests.
internal static class DiagnosticEvidenceService
{
    internal const int MaxLogBytes = 256 * 1024;

    // Standalone read-only collector: bounded metadata only, never import PHP or dump a manifest.
    internal const string PluginInventoryScript = """
import datetime, json, os, stat

def label(value):
    if not isinstance(value, (str, int, float)) or isinstance(value, bool):
        return '[not recorded]'
    text = str(value)
    text = ''.join(c if c.isprintable() else ' ' for c in text)
    return text[:160] if text else '[not recorded]'

def inventory(base):
    for server in ('HerikaServer', 'StobeServer', 'DialecticServer'):
        print('\n--- ' + server + ' ---')
        root = os.path.join(base, server, 'ext')
        try:
            if not os.path.isdir(os.path.join(base, server)):
                print('[not installed] Server directory missing')
                continue
            if os.path.islink(os.path.join(base, server)) or os.path.islink(root):
                print('[not inspected] Server/ext directory is a symbolic link')
                continue
            entries = []
            truncated = False
            with os.scandir(root) as scan:
                for i, entry in enumerate(scan):
                    if i >= 1000:
                        truncated = True
                        break
                    if not entry.name.startswith('.') and (entry.is_symlink() or entry.is_dir(follow_symlinks=False)):
                        if len(entries) >= 200:
                            truncated = True
                            break
                        entries.append(entry)
        except FileNotFoundError:
            print('[none] No ext directory')
            continue
        except OSError:
            print('[unavailable] Cannot read ext directory')
            continue
        if not entries:
            print('[none] No plugin directories found')
        for entry in sorted(entries, key=lambda e: e.name.casefold()):
            prefix = 'Folder: ' + label(entry.name)
            if entry.is_symlink():
                print(prefix + ' | [not inspected] Symbolic link')
                continue
            path = os.path.join(root, entry.name, 'manifest.json')
            try:
                fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
                with os.fdopen(fd, 'rb') as stream:
                    info = os.fstat(stream.fileno())
                    if not stat.S_ISREG(info.st_mode):
                        print(prefix + ' | [unavailable] Manifest is not a regular file')
                        continue
                    data = stream.read(65537)
                if len(data) > 65536:
                    print(prefix + ' | [unavailable] Manifest exceeds 64 KiB')
                    continue
                manifest = json.loads(data.decode('utf-8-sig'))
                if not isinstance(manifest, dict):
                    raise ValueError('object required')
                modified = datetime.datetime.fromtimestamp(info.st_mtime, datetime.timezone.utc).isoformat()
                print(prefix + ' | Name: ' + label(manifest.get('name')) +
                      ' | Version: ' + label(manifest.get('version')) +
                      ' | Channel: ' + label(manifest.get('channel')) +
                      ' | Manifest modified UTC: ' + modified)
            except FileNotFoundError:
                print(prefix + ' | [unknown metadata] No manifest.json (legacy or incomplete plugin)')
            except (ValueError, UnicodeError, RecursionError):
                print(prefix + ' | [invalid] Manifest JSON')
            except OSError:
                print(prefix + ' | [unavailable] Manifest unreadable or symbolic link')
        if truncated:
            print('[truncated] Inventory limit: 200 plugin directories / 1000 ext entries')

inventory('/var/www/html')
""";

    internal static string ReadTail(string path, int maxLines = 3000, Action? onTruncated = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var prefix = new byte[3];
        var count = stream.Read(prefix, 0, prefix.Length);
        Encoding encoding = Encoding.UTF8;
        if (count >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe) encoding = Encoding.Unicode;
        else if (count >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff) encoding = Encoding.BigEndianUnicode;
        var offset = Math.Max(0, length - MaxLogBytes);
        if (encoding != Encoding.UTF8) offset -= offset % 2;
        stream.Position = offset;
        // Snapshot the length: a live writer must not make diagnostic collection unbounded.
        var buffer = new byte[(int)Math.Min(MaxLogBytes, length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var bytes = stream.Read(buffer, read, buffer.Length - read);
            if (bytes == 0) break;
            read += bytes;
        }
        var text = encoding.GetString(buffer, 0, read).TrimStart('\uFEFF');
        if (offset > 0)
        {
            var newline = text.IndexOf('\n');
            if (newline >= 0) text = text[(newline + 1)..];
        }
        var sourceLines = text.Replace("\r", "").TrimEnd('\n').Split('\n');
        if (offset > 0 || sourceLines.Length > maxLines) onTruncated?.Invoke();
        var tail = string.Join(Environment.NewLine, sourceLines.TakeLast(maxLines));
        return (offset > 0 ? "[truncated to last 256 KiB]" + Environment.NewLine : "") + tail;
    }

    // Compare modification times, not candidate order; unreadable newest files fall back explicitly.
    internal static string? AddNewestLog(List<string> lines, string label, IEnumerable<string> candidates, int maxLines = 3000, CollectionSummary? summary = null)
    {
        lines.Add($"--- {label} ---");
        var available = new List<FileInfo>();
        foreach (var path in candidates.Select(Environment.ExpandEnvironmentVariables).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                lines.Add($"Candidate: {path} | modified UTC {info.LastWriteTimeUtc:O} | {info.Length} bytes");
                available.Add(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                lines.Add($"[unreadable candidate] {path}: {ex.GetType().Name}");
                summary?.Problems.Add(label + ": unreadable candidate");
            }
        }
        foreach (var info in available.OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var text = ReadTail(info.FullName, maxLines, () => summary?.Truncated.Add(label));
                lines.Add($"Selected newest readable log: {info.FullName}");
                lines.Add($"Modified UTC: {info.LastWriteTimeUtc:O}; collected UTC: {DateTime.UtcNow:O}");
                lines.Add(Sanitize(text));
                lines.Add("");
                return info.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"[unreadable] {info.FullName}: {ex.GetType().Name}; trying next candidate");
                summary?.Problems.Add(label + ": unreadable log; attempted fallback");
            }
        }
        lines.Add("[missing or unreadable] No recent session can be established.");
        summary?.MissingLogs.Add(label);
        lines.AddRange(candidates.Select(path => "Attempted: " + path).Take(100));
        lines.Add("");
        return null;
    }

    // Findings come from collectors, never from matching words in game/provider log content.
    internal sealed class CollectionSummary
    {
        internal HashSet<string> Problems { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> MissingServers { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> MissingLogs { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Truncated { get; } = new(StringComparer.Ordinal);

        internal List<string> Format()
        {
            var result = new List<string> { "Collection Summary", "Collection notices only, not a diagnosis of game or service health." };
            foreach (var (title, items) in new[] { ("Missing servers", MissingServers), ("Collection problems", Problems), ("Missing logs", MissingLogs), ("Truncated sections", Truncated) })
            {
                result.Add($"{title}: {items.Count}");
                result.AddRange(items.OrderBy(item => item, StringComparer.Ordinal).Take(100).Select(item => "  - " + Sanitize(item)));
                if (items.Count > 100) result.Add("  - Further entries omitted from summary; see report details.");
            }
            result.Add("Missing optional servers or logs can be normal. Details follow below.");
            result.Add("");
            return result;
        }
    }

    // Separate trusted stat metadata from log text and track actual truncation, not error words.
    internal static void AppendServerLog(List<string> lines, CollectionSummary summary, string name, string output, bool byteLimited, int maxLines)
    {
        var split = output.IndexOf('\n');
        var metadata = (split >= 0 ? output[..split] : output).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (metadata.Length != 2 || !long.TryParse(metadata[0], out var size) || size < 0 ||
            !long.TryParse(metadata[1], out var seconds) || seconds < -62135596800 || seconds > 253402300799)
        {
            summary.Problems.Add(name + ": log metadata unavailable");
            lines.Add("[unavailable] Log size and modification time could not be read.");
            return;
        }
        lines.Add($"Size bytes: {size}; Modified UTC: {DateTimeOffset.FromUnixTimeSeconds(seconds):O}; Collected UTC: {DateTimeOffset.UtcNow:O}");
        var body = split >= 0 ? output[(split + 1)..].Replace("\r", "").TrimEnd('\n') : "";
        var rows = body.Length == 0 ? Array.Empty<string>() : body.Split('\n');
        if ((byteLimited && size > MaxLogBytes) || rows.Length > maxLines)
        {
            summary.Truncated.Add(name);
            lines.Add($"[truncated] Showing up to the last {maxLines} lines" + (byteLimited ? " and 256 KiB." : "."));
        }
        lines.Add(Sanitize(string.Join(Environment.NewLine, rows.TakeLast(maxLines))));
    }

    // New network/configuration evidence must never contain URL credentials, queries, or API keys.
    internal static string Sanitize(string text)
    {
        text = Regex.Replace(text, @"https?://[^\s\""'<>]+", match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[invalid URL]";
            return uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", "", StringComparison.Ordinal) + uri.AbsolutePath;
        }, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?im)((?:authorization|api[_-]?key|access[_-]?token|password|secret)\s*[\""']?\s*[:=]\s*).*$", "$1[redacted]", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Regex.Replace(text, @"(?:hf_|sk-)[A-Za-z0-9_-]{16,}", "[redacted]", RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    internal static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork &&
            (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }

    internal static async Task<string> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var ip) || !IsLocalAddress(ip) || port is < 1 or > 65535)
            return "[not probed] Only literal loopback/private LAN addresses are probed.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new TcpClient();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
            return $"TCP connected ({clock.ElapsedMilliseconds} ms); does not verify game requests or authentication.";
        }
        catch (OperationCanceledException) { return "TCP timed out/cancelled (2-second limit)."; }
        catch (SocketException ex) { return $"TCP failed: {ex.SocketErrorCode}"; }
    }

    // Parse only connection keys, never dump an INI. Later definitions win within a single file.
    internal static string DescribePluginOverride(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) { section = line.Trim('[', ']', '\r'); continue; }
            var separator = line.IndexOf('=');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            if (key.Equals("ServerHost", StringComparison.OrdinalIgnoreCase) || key.Equals("ServerPort", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("SERVER", StringComparison.OrdinalIgnoreCase) ||
                (section.Length == 0 || section.Equals("Server", StringComparison.OrdinalIgnoreCase)) &&
                (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Port", StringComparison.OrdinalIgnoreCase) || key.Equals("Path", StringComparison.OrdinalIgnoreCase)))
            {
                var value = line[(separator + 1)..].Trim().Split(';', '#')[0].Trim().Trim('"', '\'');
                if (key.Contains("Port", StringComparison.OrdinalIgnoreCase))
                    value = int.TryParse(value, out var port) && port is > 0 and <= 65535 ? port.ToString() : "[invalid port]";
                else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                    value = value.Split('?', '#')[0];
                else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    value = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
                else if (Uri.CheckHostName(value) == UriHostNameType.Unknown)
                    value = "[invalid host; omitted]";
                fields[key] = Sanitize(value.Length <= 256 ? value : "[oversized value; omitted]");
            }
        }
        return fields.Count == 0 ? "[no recognized address override]" : string.Join("; ", fields.Select(field => $"{field.Key}={field.Value}"));
    }
}
