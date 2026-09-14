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

    internal static string ReadTail(string path, int maxLines = 3000)
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
        var tail = string.Join(Environment.NewLine, text.Replace("\r", "").Split('\n').TakeLast(maxLines));
        return (offset > 0 ? "[truncated to last 256 KiB]" + Environment.NewLine : "") + tail;
    }

    // Compare modification times, not candidate order; unreadable newest files fall back explicitly.
    internal static string? AddNewestLog(List<string> lines, string label, IEnumerable<string> candidates, int maxLines = 3000)
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
            }
        }
        foreach (var info in available.OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var text = ReadTail(info.FullName, maxLines);
                lines.Add($"Selected newest readable log: {info.FullName}");
                lines.Add($"Modified UTC: {info.LastWriteTimeUtc:O}; collected UTC: {DateTime.UtcNow:O}");
                lines.Add(Sanitize(text));
                lines.Add("");
                return info.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lines.Add($"[unreadable] {info.FullName}: {ex.GetType().Name}; trying next candidate");
            }
        }
        lines.Add("[missing or unreadable] No recent session can be established.");
        lines.AddRange(candidates.Select(path => "Attempted: " + path).Take(100));
        lines.Add("");
        return null;
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
