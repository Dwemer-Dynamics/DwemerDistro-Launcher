using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// A starter model from one of the distro catalog's recommended lists. Size is the catalog's Small/Medium/Large label
/// (the tier on older catalogs). VramGb is null when the catalog has no estimate.
/// </summary>
public sealed record LocalAiModel(string Id, string Size, string Name, string Purpose, string Quant, long Bytes, (double Low, double High)? VramGb, bool Installed)
{
    public string Facts
    {
        get
        {
            var download = Bytes > 0 ? Gigabytes(Bytes) + " download" : "Download size unknown";
            var vram = VramGb is { } range
                ? string.Create(CultureInfo.InvariantCulture, $"about {range.Low}–{range.High} GB VRAM (estimate)")
                : "VRAM not estimated";
            return (Quant.Length > 0 ? Quant + " · " : string.Empty) + $"{download} · {vram}" + (Installed ? " · Already downloaded" : string.Empty);
        }
    }

    public static string Gigabytes(long bytes) =>
        (bytes / 1e9).ToString(bytes < 1e10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " GB";
}

/// <summary>The catalog's dialogue models and optional decision models; a list is empty when the catalog lacks it.</summary>
public sealed record LocalAiCatalog(IReadOnlyList<LocalAiModel> Dialogue, IReadOnlyList<LocalAiModel> Decision);

public sealed class LmStudioService(WslService wsl)
{
    private const string Helper = "/usr/local/bin/ddistro_lmstudio";

    public async Task<JsonElement> ReadAsync(string command)
    {
        var result = await wsl.RunDistroAsUserAsync("dwemer", [Helper, command]).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException("Update Distro first, then retry LLM Studio. " + result.StandardOutput.Trim());
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.Clone();
    }

    // The catalog's dialogue (recommended) and decision lists, in catalog order.
    public async Task<LocalAiCatalog> GetCatalogAsync()
    {
        var catalog = await ReadAsync("catalog").ConfigureAwait(false);
        return new LocalAiCatalog(Models(catalog, "recommended"), Models(catalog, "decisionRecommended"));
    }

    // Entries without an id or name are left out.
    private static List<LocalAiModel> Models(JsonElement catalog, string list)
    {
        var models = new List<LocalAiModel>();
        if (!catalog.TryGetProperty(list, out var entries) || entries.ValueKind != JsonValueKind.Array)
            return models;
        foreach (var model in entries.EnumerateArray())
        {
            if (Text(model, "id") is not { Length: > 0 } id || Text(model, "name") is not { Length: > 0 } name) continue;
            (double, double)? vram = null;
            if (model.TryGetProperty("vramGB", out var range) && range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2 &&
                range[0].ValueKind == JsonValueKind.Number && range[1].ValueKind == JsonValueKind.Number)
                vram = (range[0].GetDouble(), range[1].GetDouble());
            var bytes = model.TryGetProperty("bytes", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var value) ? value : 0;
            var installed = model.TryGetProperty("installed", out var flag) && flag.ValueKind == JsonValueKind.True;
            models.Add(new LocalAiModel(id, Text(model, "size") ?? Text(model, "tier") ?? string.Empty, name, Text(model, "purpose") ?? string.Empty,
                Text(model, "quant") ?? string.Empty, bytes, vram, installed));
        }
        return models;
    }

    public async Task InstallEngineAsync(Action<string> progress)
    {
        var install = await wsl.RunDistroAsUserAsync("root", ["/usr/local/bin/install_lmstudio"], progress).ConfigureAwait(false);
        if (!install.Succeeded)
            throw new InvalidOperationException("LLM Studio installation failed. " + install.StandardError.Trim());
    }

    /// <summary>
    /// Installs the engine if needed, downloads (or finds) the dialogue model and the optional decision model, starts the
    /// engine, loads them one after the other and only then saves them as the startup pair; no decision model clears the
    /// saved one. Each step is confirmed from the helper's status before the next one, and models are addressed only by
    /// the exact keys their download jobs report. Returns those keys.
    /// </summary>
    public async Task<(string Dialogue, string? Decision)> SetUpModelsAsync(string dialogueId, string? decisionId, Action<string> progress)
    {
        var status = await ReadAsync("status").ConfigureAwait(false);
        if (!status.GetProperty("installed").GetBoolean())
            await InstallEngineAsync(progress).ConfigureAwait(false);

        var dialogue = await DownloadAsync(dialogueId, progress).ConfigureAwait(false);
        var decision = decisionId is null ? null : await DownloadAsync(decisionId, progress).ConfigureAwait(false);

        status = await ReadAsync("status").ConfigureAwait(false);
        if (!status.GetProperty("running").GetBoolean())
            await RunJobAsync(new JsonObject { ["action"] = "start" }, progress, TimeSpan.FromMinutes(10)).ConfigureAwait(false);

        // The second load is checked against the memory the first one leaves.
        await LoadAsync(dialogue, progress).ConfigureAwait(false);
        if (decision is not null)
            await LoadAsync(decision, progress).ConfigureAwait(false);
        // The first model can be evicted or unloaded while the second loads; the saved pair is kept unless both are up.
        status = await ReadAsync("status").ConfigureAwait(false);
        if (!IsLoaded(status, dialogue) || (decision is not null && !IsLoaded(status, decision)))
            throw new InvalidOperationException("LLM Studio did not keep both models loaded, so the startup models were not changed. " +
                                                "Free some memory or pick a smaller pair, then retry.");

        await RunJobAsync(new JsonObject { ["action"] = "settings", ["autostart"] = true, ["startupModel"] = dialogue, ["startupDecisionModel"] = decision ?? string.Empty },
            progress, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (!IsStartupPair(await ReadAsync("status").ConfigureAwait(false), dialogue, decision))
            throw new InvalidOperationException("The models are loaded, but their startup setting was not saved. Retry.");
        return (dialogue, decision);
    }

    // An already downloaded preset finishes at once with its key; the helper never downloads it again.
    private async Task<string> DownloadAsync(string presetId, Action<string> progress)
    {
        var download = await RunJobAsync(new JsonObject { ["action"] = "download", ["preset"] = presetId }, progress, TimeSpan.FromHours(6)).ConfigureAwait(false);
        return Text(download, "modelKey") is { Length: > 0 } key
            ? key
            : throw new InvalidOperationException("The model is downloaded, but LLM Studio has not reported its exact name yet. " +
                                                  "Retry in a moment, or load it from LLM Studio.");
    }

    private async Task LoadAsync(string key, Action<string> progress)
    {
        var status = await ReadAsync("status").ConfigureAwait(false);
        if (IsLoaded(status, key)) return;
        // Load with the same saved per-model defaults that startup uses.
        var load = new JsonObject();
        if (status.TryGetProperty("modelDefaults", out var defaults) && defaults.ValueKind == JsonValueKind.Object &&
            defaults.TryGetProperty(key, out var saved) && saved.ValueKind == JsonValueKind.Object)
            load = JsonNode.Parse(saved.GetRawText())!.AsObject();
        load["action"] = "load";
        load["model"] = key;
        await RunJobAsync(load, progress, TimeSpan.FromMinutes(15)).ConfigureAwait(false);
        if (!IsLoaded(await ReadAsync("status").ConfigureAwait(false), key))
            throw new InvalidOperationException("LLM Studio finished, but the model is not loaded. Check memory use, then retry.");
    }

    // A missing startupDecisionModel, as saved by older helpers, means no decision model.
    public static bool IsStartupPair(JsonElement status, string dialogueKey, string? decisionKey) =>
        status.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty("autostart", out var autostart) && autostart.ValueKind == JsonValueKind.True &&
        Text(settings, "startupModel") == dialogueKey && (Text(settings, "startupDecisionModel") ?? string.Empty) == (decisionKey ?? string.Empty);

    private static bool IsLoaded(JsonElement status, string key) =>
        status.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array &&
        models.EnumerateArray().Any(model => Text(model, "key") == key &&
            model.TryGetProperty("loaded_instances", out var instances) && instances.ValueKind == JsonValueKind.Array && instances.GetArrayLength() > 0);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Submits one helper job and follows it to the end. Returns the finished job; a failed or replaced job throws.
    private async Task<JsonElement> RunJobAsync(JsonObject request, Action<string> progress, TimeSpan timeout)
    {
        var result = await wsl.RunDistroAsUserWithInputAsync("dwemer", [Helper, "submit"], request.ToJsonString()).ConfigureAwait(false);
        JsonElement submitted;
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            submitted = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Update Distro first, then retry LLM Studio. " + result.StandardOutput.Trim());
        }
        if (!result.Succeeded)
            throw new InvalidOperationException(Text(submitted, "error") ?? "LLM Studio could not start the operation.");
        var jobId = Text(submitted, "id");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000).ConfigureAwait(false);
            var job = (await ReadAsync("status").ConfigureAwait(false)).GetProperty("job");
            if (Text(job, "id") != jobId)
                throw new InvalidOperationException("The operation changed. Check LLM Studio before retrying.");
            var message = Text(job, "message") ?? "Working...";
            if (job.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number && total.GetInt64() > 0 &&
                job.TryGetProperty("downloaded", out var downloaded) && downloaded.ValueKind == JsonValueKind.Number)
                message += $" {downloaded.GetInt64() * 100 / total.GetInt64()}%";
            switch (Text(job, "state"))
            {
                case "completed": return job;
                case "failed": throw new InvalidOperationException(message);
            }
            progress(message);
        }
        throw new InvalidOperationException("LLM Studio is still working. Check LLM Studio before retrying.");
    }

    public async Task OpenManagerAsync()
    {
        var access = await ReadAsync("access").ConfigureAwait(false);
        var ip = await wsl.GetWslIpAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ip)) throw new InvalidOperationException("Could not find the WSL address.");
        var apache = await wsl.RunDistroAsUserAsync("root", ["/etc/init.d/apache2", "start"]).ConfigureAwait(false);
        if (!apache.Succeeded) throw new InvalidOperationException("Could not start the Dashboard web server.");
        // The fragment is never sent in an HTTP request or written to the Apache access log.
        new ProcessRunner().OpenExternalUrl($"http://{ip}:8081/Dwemer-Dashboard/lmstudio.php#token={Uri.EscapeDataString(access.GetProperty("token").GetString()!)}");
    }
}
