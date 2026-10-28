using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed record SharedKey([property: JsonPropertyName("key_id")] string Id, string Provider, string Label,
    string Origin, long Revision, List<KeyUsage>? Usage, string? Preview = null, bool Configured = true, [property: JsonPropertyName("test_supported")] bool TestSupported = false)
{
    public string Display => $"{Label} · {Provider} · {Id[..Math.Min(8, Id.Length)]}";
}
public sealed record KeyUsage([property: JsonPropertyName("connection_id")] long Id, string Label, string Status);
public sealed record KeyConnection([property: JsonPropertyName("connection_id")] long Id, string Label, string Kind,
    long Revision, [property: JsonPropertyName("api_key_id")] string? KeyId,
    [property: JsonPropertyName("credential_source")] string CredentialSource, string? Preview = null)
{
    public string Display => $"{Label} · {Kind} · {CredentialSource}";
}
public sealed record LegacyKeyTarget(string Product, string Table, long Id, string Label,
    [property: JsonPropertyName("key_id")] string? KeyId,
    [property: JsonPropertyName("badge_id")] string? BadgeId, bool Configured, bool Available, string? Reason, string? Version = null, string? Preview = null)
{
    public string Identity => $"{Product}/{Table}/{Id}";
    public string Display => $"{Product} · {Label} · {Table switch { "core_api_badge" => "saved badge", "core_llm_connector" => "model", _ => "voice" }} #{Id}";
}
public sealed record KeyProvider(string Kind, string? Origin, [property: JsonPropertyName("test_supported")] bool TestSupported = false);
public sealed record SharedKeysData(List<SharedKey>? Keys, List<KeyConnection>? Connections, List<string>? Warnings,
    List<LegacyKeyTarget>? Legacy = null, Dictionary<string,KeyProvider>? Providers = null);
public sealed record KeyValidationError(string Key, string Reason);
public sealed record ApiKeysResult(bool Ok, string? Error, string? Outcome, JsonElement? Data, List<KeyValidationError>? Errors = null)
{
    public SharedKeysData? Snapshot => Outcome == "listed" && Data.HasValue ? Data.Value.Deserialize<SharedKeysData>(ApiKeysService.JsonOptions) : null;
}

/// <summary>Fixed local operations over CHIM 4's shared-key service. Secrets travel on stdin only.</summary>
public sealed class ApiKeysService(WslService wsl, Func<bool>? tryBeginActivity = null, Action? endActivity = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public async Task<ApiKeysResult> RunAsync(string operation, object? payload = null)
    {
        if (operation is not ("status" or "create" or "edit" or "test" or "delete" or "bind" or "import-environment" or "import-legacy" or "bind-legacy" or "native-status" or "save-direct" or "create-direct" or "save-connection" or "reveal-direct" or "reveal-connection" or "global-status" or "global-save" or "global-reveal" or "global-test"))
            throw new ArgumentException("Unknown API key operation.", nameof(operation));
        if (tryBeginActivity is not null && !tryBeginActivity())
            return new(false, "Wait for distro maintenance to finish, then reload API Keys.", "busy", null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            string[] args = ["/usr/local/bin/ddistro_keys", operation];
            var result = operation is "status" or "native-status" or "global-status"
                ? await wsl.RunDistroAsUserAsync("root", args, cancellationToken: timeout.Token)
                : await wsl.RunDistroAsUserWithInputAsync("root", args, JsonSerializer.Serialize(payload ?? new { }), cancellationToken: timeout.Token);
            if (result.StandardOutput.Length > 262144) return Unavailable();
            return JsonSerializer.Deserialize<ApiKeysResult>(result.StandardOutput, JsonOptions) ?? Unavailable();
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            return Unavailable();
        }
        finally { if (tryBeginActivity is not null) endActivity?.Invoke(); }
    }
    private static ApiKeysResult Unavailable() => new(false,
        "Shared API Keys is unavailable. Reload before retrying; a write may have completed. Existing assignments are preserved.", "unavailable", null);
}
