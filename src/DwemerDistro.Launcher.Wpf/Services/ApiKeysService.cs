using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed record ApiKeyProvider(string Id, string Label, bool Configured, string Source = "none");
public sealed record ApiKeyTarget(string Id, string Label, bool Available, string Reason, List<ApiKeyProvider> Providers);
public sealed record ApiKeysResult(bool Ok, string? Error, List<ApiKeyProvider>? Providers,
    List<ApiKeyTarget>? Targets, List<string>? Warnings);

/// <summary>Root-local credential management. Saved secrets never come back to the launcher.</summary>
public sealed class ApiKeysService(WslService wsl)
{
    public async Task<ApiKeysResult> RunAsync(string operation, string? provider = null, string? target = null, string? key = null)
    {
        if (operation is not ("status" or "set-shared" or "set-override" or "use-shared" or "remove-shared"))
            throw new ArgumentException("Unknown API key operation.", nameof(operation));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            Models.CommandResult result;
            string[] args = ["/usr/local/bin/ddistro_keys", operation];
            if (operation == "status")
                result = await wsl.RunDistroAsUserAsync("root", args, cancellationToken: timeout.Token);
            else
            {
                var payload = new Dictionary<string, string?> { ["provider"] = provider };
                if (target is not null) payload["target"] = target;
                if (key is not null) payload["key"] = key;
                result = await wsl.RunDistroAsUserWithInputAsync("root", args, JsonSerializer.Serialize(payload), cancellationToken: timeout.Token);
            }
            if (result.StandardOutput.Length > 262144) return Unavailable();
            var parsed = JsonSerializer.Deserialize<ApiKeysResult>(result.StandardOutput, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed ?? Unavailable();
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            // Raw command/parser diagnostics can contain input. Return only our own generic message.
            return Unavailable();
        }
    }

    private static ApiKeysResult Unavailable() => new(false,
        "API Keys is unavailable. Update Core or check that the distro is running, then refresh.", null, null, null);
}
