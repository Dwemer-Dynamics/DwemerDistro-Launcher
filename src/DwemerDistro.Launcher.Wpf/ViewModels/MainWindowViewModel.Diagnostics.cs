using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed partial class MainWindowViewModel
{
    // This section never starts a stopped distro or changes discovery/configuration.
    private async Task AddConnectionEvidenceAsync(List<string> lines)
    {
        lines.Add("Connection Evidence (read-only snapshot; TCP success is not a gameplay test)");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ownsActivity = TryBeginPassiveDistroActivity();
        if (!ownsActivity)
        {
            lines.Add("[skipped] Critical distro maintenance is running.");
            return;
        }
        try
        {
            var pcAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => (Adapter: adapter.Name, Address: address.Address)))
                .ToArray();
            foreach (var address in pcAddresses) lines.Add($"This PC: {address.Address} ({address.Adapter})");
            if (pcAddresses.Length == 0) lines.Add("This PC: [no active IPv4 address]");
            var running = await _wsl.DistroRunningAsync(timeout.Token).ConfigureAwait(false);
            var wslIp = running ? await _wsl.GetWslIpAsync(timeout.Token).ConfigureAwait(false) : null;
            lines.Add($"WSL address: {wslIp ?? "[unavailable; distro stopped, missing, or status check failed]"}");
            var targets = new HashSet<(string Host, int Port)>();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
            foreach (var (game, port) in new[] { ("skyrim", LauncherConstants.SkyrimServerPort), ("stobe", LauncherConstants.StobeServerPort), ("dialectic", LauncherConstants.DialecticServerPort), ("reign", LauncherConstants.ReignServerPort) })
            {
                foreach (var host in new[] { "127.0.0.1", wslIp }.OfType<string>()) targets.Add((host, port));
                foreach (var address in pcAddresses.Where(a => DiagnosticEvidenceService.IsLocalAddress(a.Address)).Take(3))
                    targets.Add((address.Address.ToString(), port));
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{LauncherConstants.DiscoveryPort}/discover?game={game}", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    var buffer = new byte[128];
                    var count = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: timeout.Token).ConfigureAwait(false);
                    var endpoint = System.Text.Encoding.UTF8.GetString(buffer, 0, count).Trim();
                    lines.Add($"Launcher discovery ({game}): HTTP {(int)response.StatusCode}; {(IPEndPoint.TryParse(endpoint, out var validEndpoint) ? validEndpoint.ToString() : "[invalid endpoint response]")}");
                    if (response.IsSuccessStatusCode && IPEndPoint.TryParse(endpoint, out var parsed)) targets.Add((parsed.Address.ToString(), parsed.Port));
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                {
                    lines.Add($"Launcher discovery ({game}): [unavailable] {ex.GetType().Name}");
                }
            }
            var windowsResults = await Task.WhenAll(targets.Select(async target =>
                $"Windows -> {target.Host}:{target.Port}: {await DiagnosticEvidenceService.ProbeTcpAsync(target.Host, target.Port, timeout.Token).ConfigureAwait(false)}")).ConfigureAwait(false);
            lines.AddRange(windowsResults);

            if (running)
            {
                // Only validated literal local addresses enter these fixed TCP checks; no INI text is executed.
                var safeTargets = targets.Where(target => IPAddress.TryParse(target.Host, out var ip) && DiagnosticEvidenceService.IsLocalAddress(ip) && target.Port is > 0 and <= 65535).Take(15);
                var commands = safeTargets.Select(target =>
                    $"printf '%s ' 'WSL -> {target.Host}:{target.Port}:'; timeout 1 bash -c ': </dev/tcp/{target.Host}/{target.Port}' 2>/dev/null; echo tcp_exit=$?");
                var result = await _wsl.RunBashAsync(string.Join("; ", commands), loginShell: false, cancellationToken: timeout.Token).ConfigureAwait(false);
                lines.Add(result.StandardOutput);
                lines.Add("WSL TCP exit: 0=connected, 124=timed out, other=unreachable/error; no application request was sent.");
                if (!result.Succeeded) lines.Add($"WSL probe command exit: {result.ExitCode}");
            }
        }
        catch (OperationCanceledException) { lines.Add("[timeout] Connection evidence exceeded its 30-second budget; remaining probes skipped."); }
        catch (Exception ex) { lines.Add($"[unavailable] Connection evidence: {ex.GetType().Name}"); }
        finally { EndPassiveDistroActivity(); }

        lines.Add("Plugin override candidates (physical files only; MO2/Vortex virtual overrides may differ; not proof of the loaded configuration)");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in GetSteamLibraryPaths())
            foreach (var game in new[] { "Skyrim Special Edition", "SkyrimVR", "Skyrim", "Fallout New Vegas", "Kenshi" })
                roots.Add(Path.Combine(library, "steamapps", "common", game));
        foreach (var processName in new[] { "SkyrimSE", "SkyrimVR", "TESV", "FalloutNV", "Kenshi_x64" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try { if (Path.GetDirectoryName(process.MainModule?.FileName) is { } root) roots.Add(root); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { lines.Add($"[unavailable] {processName} installation path"); }
                }
            }
        }
        var found = false;
        foreach (var root in roots)
            foreach (var relative in new[] { @"Data\SKSE\Plugins\AIAgent.ini", @"Data\NVSE\Plugins\dialectic.ini", @"Data\NVSE\Plugins\dialectic_custom.ini", @"mods\Stobe\Stobe.ini", @"RE_Kenshi\mods\Stobe\Stobe.ini" })
            {
                var path = Path.Combine(root, relative);
                if (!File.Exists(path)) continue;
                found = true;
                try { lines.Add($"{path} | modified UTC {File.GetLastWriteTimeUtc(path):O}: {DiagnosticEvidenceService.DescribePluginOverride(DiagnosticEvidenceService.ReadTail(path))}"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lines.Add($"[unreadable] {path}"); }
            }
        if (!found) lines.Add("[not found] No physical plugin INI found in known Steam/running-game paths. Check the selected game log for the address actually used.");
        lines.Add("");
    }

    private async Task AddUpdateInstallEvidenceAsync(List<string> lines)
    {
        lines.Add("Update / Install Evidence");
        lines.Add($"Selected launcher branches: CHIM={TargetHerikaBranch}; STOBE={TargetStobeBranch}; DIALECTIC={TargetDialecticBranch}; REIGN={ReignManager.SelectedBranch}");
        lines.Add("Persistent logs retain previous operations; use their timestamps. Missing logs may predate this launcher.");
        try
        {
            var operationsPath = Path.Combine(AppContext.BaseDirectory, "Logs", "launcher-operations.log");
            var lastOperation = File.Exists(operationsPath)
                ? DiagnosticEvidenceService.ReadTail(operationsPath).Split('\n').LastOrDefault(line => line.Contains("] START ") || line.Contains("] END "))
                : null;
            lines.Add("Last recorded lifecycle event: " + (lastOperation is null ? "[not recorded; see timestamped installer logs below]" : DiagnosticEvidenceService.Sanitize(lastOperation)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lines.Add("Last recorded lifecycle event: [log unreadable]"); }
        foreach (var name in new[] { "launcher-startup.log", "launcher-update.log", "quickstart-install.log", "launcher-operations.log", "launcher-operations.previous.log" })
            DiagnosticEvidenceService.AddNewestLog(lines, name, [Path.Combine(AppContext.BaseDirectory, "Logs", name)]);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _httpClient.GetAsync(SystemReleaseManifestUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            lines.Add($"Published distro manifest HTTP: {(int)response.StatusCode}");
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[16385];
                var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: timeout.Token).ConfigureAwait(false);
                if (count > 16384) lines.Add("[invalid] Published manifest exceeds 16 KiB.");
                else
                {
                    var text = System.Text.Encoding.UTF8.GetString(buffer, 0, count);
                    using var manifest = JsonDocument.Parse(text);
                    lines.Add("Published distro version: " + (ParseSystemReleaseVersion(text) ?? "[invalid or missing version]"));
                    lines.Add("Published distro manifest: " + DiagnosticEvidenceService.Sanitize(manifest.RootElement.GetRawText()));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { lines.Add($"[unavailable] Published distro manifest: {ex.GetType().Name}"); }

        if (!TryBeginPassiveDistroActivity()) { lines.Add("[skipped] Distro evidence unavailable during maintenance."); return; }
        try
        {
            if (!await _wsl.DistroRunningAsync(timeout.Token).ConfigureAwait(false))
            { lines.Add("[unavailable] Installed marker/repositories: distro is stopped, missing, or its status check failed."); return; }
            var marker = await _wsl.RunBashAsync($"head -c 16385 {InstalledSystemReleaseManifestPath}", loginShell: false, cancellationToken: timeout.Token).ConfigureAwait(false);
            lines.Add("Installed distro version: " + (marker.Succeeded && marker.StandardOutput.Length <= 16384
                ? ParseSystemReleaseVersion(marker.StandardOutput) ?? "[invalid or missing version in marker]"
                : $"[marker read failed or oversized; exit {marker.ExitCode}]"));
            if (!string.IsNullOrWhiteSpace(marker.StandardError)) lines.Add("Marker read error: " + DiagnosticEvidenceService.Sanitize(marker.StandardError));
            var command = "printf '%s\\n' 'Installed distro manifest:'; " +
                $"if [ ! -e {InstalledSystemReleaseManifestPath} ]; then echo '[missing] {InstalledSystemReleaseManifestPath}'; " +
                $"elif [ ! -r {InstalledSystemReleaseManifestPath} ]; then echo '[unreadable] {InstalledSystemReleaseManifestPath}'; " +
                $"else head -c 16384 {InstalledSystemReleaseManifestPath}; echo; fi; " +
                "echo 'Launcher sync marker:'; head -c 256 /home/dwemer/.launcher_synced_version 2>&1; echo; " +
                "for repo in /home/dwemer/dwemerdistro /var/www/html/HerikaServer /var/www/html/StobeServer /var/www/html/DialecticServer /var/www/html/ReignServer; do " +
                "echo \"Repository: $repo\"; if [ -d \"$repo/.git\" ]; then " +
                "git -C \"$repo\" rev-parse --abbrev-ref HEAD; git -C \"$repo\" remote get-url origin; git -C \"$repo\" rev-parse HEAD; " +
                "else echo '[not installed or no Git metadata]'; fi; done; " +
                "count=0; for log in /home/dwemer/.dwemerdistro/logs/components/*.log; do " +
                "[ -f \"$log\" ] || continue; count=$((count+1)); if [ $count -gt 30 ]; then echo '[remaining component logs omitted: 30-file limit]'; break; fi; echo \"Component installer: $log\"; stat -c 'Modified: %y; Bytes: %s' \"$log\"; tail -c 65536 \"$log\" | tail -n 300; done";
            var result = await _wsl.RunBashAsync(command, loginShell: false, cancellationToken: timeout.Token).ConfigureAwait(false);
            lines.Add(DiagnosticEvidenceService.Sanitize(result.StandardOutput));
            if (!string.IsNullOrWhiteSpace(result.StandardError)) lines.Add("[stderr] " + DiagnosticEvidenceService.Sanitize(result.StandardError));
            lines.Add($"Evidence command exit: {result.ExitCode}");
        }
        catch (OperationCanceledException) { lines.Add("[timeout] Update/install evidence exceeded its 20-second budget."); }
        catch (Exception ex) { lines.Add($"[unavailable] Update/install evidence: {ex.GetType().Name}"); }
        finally { EndPassiveDistroActivity(); lines.Add(""); }
    }
}
