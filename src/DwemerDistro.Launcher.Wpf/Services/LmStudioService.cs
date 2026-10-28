using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed record LocalAiOption(string Id, string Name)
{
    public override string ToString() => Name;
}

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

    public async Task<IReadOnlyList<LocalAiOption>> GetOptionsAsync()
    {
        var catalog = await ReadAsync("catalog").ConfigureAwait(false);
        return catalog.GetProperty("models").EnumerateArray()
            .Select(model => new LocalAiOption(model.GetProperty("id").GetString()!, model.GetProperty("name").GetString()!))
            .ToArray();
    }

    public async Task InstallAsync(string option, Action<string> progress)
    {
        var install = await wsl.RunDistroAsUserAsync("root", ["/usr/local/bin/install_lmstudio"], progress).ConfigureAwait(false);
        if (!install.Succeeded)
            throw new InvalidOperationException("LLM Studio installation failed. " + install.StandardError.Trim());
        if (option == "engine") return;
        var result = await wsl.RunDistroAsUserWithInputAsync("dwemer", [Helper, "submit"],
            JsonSerializer.Serialize(new { action = "download", preset = option })).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (!result.Succeeded)
            throw new InvalidOperationException(document.RootElement.GetProperty("error").GetString());
        var jobId = document.RootElement.GetProperty("id").GetString();
        var deadline = DateTime.UtcNow.AddHours(6);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000).ConfigureAwait(false);
            var state = await ReadAsync("status").ConfigureAwait(false);
            var job = state.GetProperty("job");
            if (job.GetProperty("id").GetString() != jobId)
                throw new InvalidOperationException("The operation changed. Check LLM Studio Manager before retrying.");
            var message = job.GetProperty("message").GetString() ?? "Working...";
            if (job.TryGetProperty("total", out var total) && total.GetInt64() > 0)
                message += $" {job.GetProperty("downloaded").GetInt64() * 100 / total.GetInt64()}%";
            progress(message);
            switch (job.GetProperty("state").GetString())
            {
                case "completed": return;
                case "failed": throw new InvalidOperationException(message);
            }
        }
        throw new InvalidOperationException("Download is still running. Check LLM Studio Manager before retrying.");
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
