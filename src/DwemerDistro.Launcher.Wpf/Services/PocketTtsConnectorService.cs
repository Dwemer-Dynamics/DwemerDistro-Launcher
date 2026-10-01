using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// Client for the DwemerDistro Core helper that keeps DwemerDistro-managed PocketTTS
/// connectors on the enabled backend. The helper owns backend resolution, probing, and
/// all database writes; the launcher only chooses the backend and shows the result.
/// </summary>
public sealed class PocketTtsConnectorService(WslService wsl)
{
    public const string HelperPath = "/usr/local/bin/sync_pockettts_connectors";
    public const string StartupResultPrefix = "DDISTRO_POCKETTTS_CONNECTORS=";
    private const int CommandNotFoundExitCode = 127;

    // One helper run at a time; repeated callbacks wait instead of racing the same rows.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static string? BackendForComponent(string? componentKey)
    {
        return (componentKey ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "audiocpp" => "audiocpp",
            "pockettts" => "python",
            _ => null
        };
    }

    public static string? BackendForPreset(SetupPreset preset)
    {
        return preset.ComponentKeys.Select(BackendForComponent).FirstOrDefault(backend => backend is not null);
    }

    public async Task<PocketTtsConnectorResult> SyncAsync(string backend, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await wsl.RunDistroAsUserAsync(
                    LauncherConstants.DistroUser,
                    new[] { HelperPath, "--json", "--backend", backend },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var parsed = Parse(result.StandardOutput);
            if (parsed is not null)
            {
                return parsed;
            }

            return result.ExitCode == CommandNotFoundExitCode
                ? PocketTtsConnectorResult.CoreUpdateRequired()
                : PocketTtsConnectorResult.Failed("The connector check could not finish.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PocketTtsConnectorResult.Failed("The connector check could not start.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool TryParseStartupLine(string line, out PocketTtsConnectorResult? result)
    {
        result = null;
        var trimmed = (line ?? string.Empty).Trim();
        if (!trimmed.StartsWith(StartupResultPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        result = Parse(trimmed[StartupResultPrefix.Length..])
                 ?? PocketTtsConnectorResult.Failed("The startup connector result could not be read.");
        return true;
    }

    internal static PocketTtsConnectorResult? Parse(string? json)
    {
        var text = (json ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ResultPayload>(text, JsonOptions);
            if (payload is null || payload.Contract < 1 || string.IsNullOrWhiteSpace(payload.State))
            {
                return null;
            }

            var products = (payload.Products ?? [])
                .Select(product => new PocketTtsConnectorProductStatus(
                    product.Name ?? product.Product ?? "Unknown",
                    (product.Status ?? "failed").Trim().ToLowerInvariant(),
                    product.Message ?? string.Empty))
                .ToArray();
            return new PocketTtsConnectorResult(
                payload.State.Trim().ToLowerInvariant(),
                payload.Message ?? string.Empty,
                products);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class ResultPayload
    {
        public int Contract { get; set; }

        public string? State { get; set; }

        public string? Message { get; set; }

        public List<ProductPayload>? Products { get; set; }
    }

    private sealed class ProductPayload
    {
        public string? Product { get; set; }

        public string? Name { get; set; }

        public string? Status { get; set; }

        public string? Message { get; set; }
    }
}

/// <summary>Product status is updated, already, preserved, pending, none, missing, or failed.</summary>
public sealed record PocketTtsConnectorProductStatus(string Name, string Status, string Message)
{
    public string ShortText => Status switch
    {
        "updated" or "already" => "connected",
        "preserved" => "custom kept",
        "pending" => "not changed yet",
        "none" => "no Pocket-TTS connector",
        "missing" => "not installed",
        _ => "needs attention"
    };
}

/// <summary>
/// State is ok, ambiguous, not_enabled, service_unavailable, or wrong_provider from the helper,
/// plus core_update_required and failed from the launcher.
/// </summary>
public sealed record PocketTtsConnectorResult(
    string State,
    string Message,
    IReadOnlyList<PocketTtsConnectorProductStatus> Products)
{
    public bool NeedsAttention =>
        State is not ("ok" or "not_enabled")
        || Products.Any(product => product.Status is "pending" or "failed");

    public string Summary
    {
        get
        {
            if (State == "core_update_required")
            {
                return Message;
            }

            var products = string.Join(", ", Products.Select(product => $"{product.Name} {product.ShortText}"));
            return products.Length == 0 ? Message : $"{Message} Game connectors: {products}.";
        }
    }

    public static PocketTtsConnectorResult CoreUpdateRequired()
    {
        return new PocketTtsConnectorResult(
            "core_update_required",
            "Update DwemerDistro so game voice connectors can follow Pocket-TTS automatically. Connectors were not changed.",
            []);
    }

    public static PocketTtsConnectorResult Failed(string message)
    {
        return new PocketTtsConnectorResult("failed", message + " Connectors were not changed.", []);
    }
}
