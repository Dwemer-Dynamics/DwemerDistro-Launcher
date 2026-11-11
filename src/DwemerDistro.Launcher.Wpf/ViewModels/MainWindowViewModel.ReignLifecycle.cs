using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.Views;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// Open REIGN: the webpage opens only once Reign's public health check passes. A running distro
/// with a stopped Reign starts Reign alone through <c>ddistro_reign start</c>; DwemerDistro and the
/// other servers are never restarted for it.
/// </summary>
public sealed partial class MainWindowViewModel
{
    internal const string ReignRuntimeCommand = "/usr/local/bin/ddistro_reign";
    internal const string ReignServerLogPath = "/var/www/html/ReignServer/data/logs/server.log";
    internal static readonly string ReignHealthUrl = $"http://127.0.0.1:{LauncherConstants.ReignServerPort}/health";

    // ddistro_reign waits up to 30 s for its runtime lock and 45 s for health, after a permissions pass.
    private static readonly TimeSpan ReignStartTimeout = TimeSpan.FromSeconds(150);
    // Apache can lag the loopback listener briefly after the runtime reports healthy.
    private static readonly TimeSpan ReignPublicHealthSettleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReignLogReadTimeout = TimeSpan.FromSeconds(10);
    private const int ReignHealthBodyLimit = 65536;

    /// <summary>Argument vector, never a shell string: nothing user-supplied reaches the command.</summary>
    internal static string[] BuildReignStartArguments()
    {
        return ["sudo", ReignRuntimeCommand, "start"];
    }

    /// <summary>
    /// Mirrors <c>ddistro_reign start</c>'s own acceptance for the current ReignServer deployment: the Reign
    /// service on protocol 1 reporting a current database schema that matches its required version.
    /// </summary>
    internal static bool IsReignHealthPayloadHealthy(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("service", out var service) || service.ValueKind != JsonValueKind.String
                || service.GetString() != "BannerlordReignServer"
                || !root.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.Number
                || !protocol.TryGetInt32(out var protocolVersion) || protocolVersion != 1)
            {
                return false;
            }

            return root.TryGetProperty("databaseSchemaVersion", out var schema)
                   && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version > 0
                   && root.TryGetProperty("requiredDatabaseSchemaVersion", out var required)
                   && required.ValueKind == JsonValueKind.Number && required.TryGetInt32(out var requiredVersion)
                   && version == requiredVersion
                   && root.TryGetProperty("databaseSchemaUpToDate", out var upToDate)
                   && upToDate.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>One bounded probe of the public (Apache-proxied) health endpoint.</summary>
    private async Task<bool> IsReignHealthyAsync()
    {
        try
        {
            using var probeCts = new CancellationTokenSource(ServerWebPageProbeTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, ReignHealthUrl);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > ReignHealthBodyLimit)
            {
                return false;
            }

            var body = await ReadBoundedBodyAsync(response.Content, ReignHealthBodyLimit, probeCts.Token).ConfigureAwait(false);
            return IsReignHealthPayloadHealthy(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException
                                       or UriFormatException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes as UTF-8, or returns null when the body is larger.
    /// The read itself is bounded, so a response without Content-Length never buffers more.
    /// </summary>
    internal static async Task<string?> ReadBoundedBodyAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[limit + 1];
        var total = 0;
        int read;
        while (total < buffer.Length
               && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total > limit ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    private async Task<bool> WaitForReignHealthAsync(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (await IsReignHealthyAsync().ConfigureAwait(true))
            {
                return true;
            }

            if (elapsed.Elapsed + TimeSpan.FromSeconds(1) >= timeout)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The distro is running but Reign is not healthy: offer to start Reign alone, then open the
    /// page only after the public health check passes.
    /// </summary>
    private async Task StartReignAndOpenAsync(string url)
    {
        if (!ReignManager.CanUseInstalledFeatures)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            "Reign is not running.\n\nStart Reign now? DwemerDistro and the other servers keep running.",
            "REIGN Webpage",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        // The prompt is modal; recheck what it may have changed.
        if (!ReignManager.CanUseInstalledFeatures || !IsServerRunning)
        {
            return;
        }

        if (await IsReignHealthyAsync().ConfigureAwait(true))
        {
            _processRunner.OpenExternalUrl(url);
            return;
        }

        ReignManager.BeginOperation("Starting Reign...");
        AppendLog($"{Environment.NewLine}Starting Reign...{Environment.NewLine}", "green");

        string? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(ReignStartTimeout);
            var result = await _wsl.RunDistroAsUserAsync(
                    LauncherConstants.DistroUser,
                    BuildReignStartArguments(),
                    AppendOperationOutput,
                    timeout.Token)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                failure = GetCommandError(result);
            }
            else if (!await WaitForReignHealthAsync(ReignPublicHealthSettleTimeout).ConfigureAwait(true))
            {
                failure = "Reign started, but its health check at " + ReignHealthUrl + " did not pass.";
            }
        }
        catch (OperationCanceledException)
        {
            failure = $"Reign did not finish starting within {ReignStartTimeout.TotalSeconds:0} seconds.";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            ReignManager.EndOperation(failure is null ? null : "Last start failed");
        }

        if (failure is null)
        {
            AppendLog($"Reign is running.{Environment.NewLine}", "green");
            _processRunner.OpenExternalUrl(url);
            return;
        }

        AppendLog($"Reign start failed: {failure}{Environment.NewLine}", "red");
        await ShowReignStartFailureAsync(failure).ConfigureAwait(true);
    }

    private async Task ShowReignStartFailureAsync(string reason)
    {
        var logTail = await ReadReignServerLogTailAsync().ConfigureAwait(true);
        ShowOwnedServerDialog(BuildReignStartFailureContent(reason, logTail));
    }

    // The data directory is owned by the application user, so no root shell is needed to read it.
    private async Task<string?> ReadReignServerLogTailAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReignLogReadTimeout);
            var result = await _wsl.RunDistroAsUserAsync(
                    LauncherConstants.DistroUser,
                    ["tail", "-c", "8192", ReignServerLogPath],
                    cancellationToken: timeout.Token)
                .ConfigureAwait(true);
            return result.Succeeded ? DiagnosticEvidenceService.Sanitize(result.StandardOutput) : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static ServerUpdateDialogContent BuildReignStartFailureContent(string reason, string? logTail)
    {
        // Same bounds as the update failure dialog: the console and the log file keep everything.
        const int maxLines = 12;
        const int maxCharacters = 1200;
        var lines = (logTail ?? string.Empty).Trim().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - maxLines)));
        if (tail.Length > maxCharacters)
        {
            tail = "..." + tail[^maxCharacters..];
        }

        var shownReason = reason.Trim();
        if (shownReason.Length > 400)
        {
            shownReason = shownReason[..400] + "...";
        }

        return new ServerUpdateDialogContent(
            Title: "Reign did not start",
            Heading: "The REIGN Webpage was not opened.",
            Intro: shownReason,
            DetailsLabel: "Last Reign server log lines",
            Details: tail.Length > 0 ? tail : "(the Reign server log is empty or could not be read)",
            YesChoice: null,
            NoChoice: null,
            Footnote: $"Full log: {ReignServerLogPath}. DwemerDistro and the other servers were not restarted.",
            IsWarning: false);
    }
}
