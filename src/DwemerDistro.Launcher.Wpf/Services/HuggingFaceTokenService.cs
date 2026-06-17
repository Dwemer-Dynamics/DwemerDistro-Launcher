using System.Text.Json;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class HuggingFaceTokenService(WslService wsl)
{
    public const string TokenPath = "/home/dwemer/.cache/huggingface/token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<HuggingFaceTokenStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", ProbeStatusScript },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return HuggingFaceTokenStatus.Unknown(
                $"Unable to check {TokenPath}: {BuildErrorText(result)}");
        }

        ProbeResult? probe;
        try
        {
            probe = JsonSerializer.Deserialize<ProbeResult>(result.StandardOutput.Trim(), JsonOptions);
        }
        catch (JsonException)
        {
            probe = null;
        }

        if (probe is null)
        {
            return HuggingFaceTokenStatus.Unknown("Unable to parse Hugging Face token status.");
        }

        return new HuggingFaceTokenStatus(
            probe.Configured,
            probe.Valid,
            probe.UserName,
            probe.Error,
            string.IsNullOrWhiteSpace(probe.TokenSource) ? TokenPath : probe.TokenSource);
    }

    public async Task<string?> ReadTokenAsync(CancellationToken cancellationToken = default)
    {
        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", ReadTokenScript },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    public Task<CommandResult> SaveTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var normalizedToken = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return ClearTokenAsync(cancellationToken);
        }

        return wsl.RunDistroAsUserWithInputAsync(
            LauncherConstants.DistroUser,
            new[] { "python3", "-c", SaveTokenScript },
            normalizedToken + "\n",
            cancellationToken: cancellationToken);
    }

    public Task<CommandResult> ClearTokenAsync(CancellationToken cancellationToken = default)
    {
        return wsl.RunDistroAsUserAsync(
            LauncherConstants.DistroUser,
            new[] { "python3", "-c", ClearTokenScript },
            cancellationToken: cancellationToken);
    }

    public static string BuildErrorText(CommandResult result)
    {
        var text = (result.StandardError + result.StandardOutput).Trim();
        return string.IsNullOrWhiteSpace(text) ? $"Exit code {result.ExitCode}" : text;
    }

    private const string ReadTokenScript = """
from pathlib import Path

path = Path.home() / ".cache" / "huggingface" / "token"
if path.exists():
    print(path.read_text(encoding="utf-8").strip())
""";

    private const string SaveTokenScript = """
from pathlib import Path
import os
import sys

token = sys.stdin.read().strip()
path = Path.home() / ".cache" / "huggingface" / "token"
path.parent.mkdir(parents=True, exist_ok=True)
path.write_text(token + "\n", encoding="utf-8")
os.chmod(path, 0o600)
""";

    private const string ClearTokenScript = """
from pathlib import Path

path = Path.home() / ".cache" / "huggingface" / "token"
path.unlink(missing_ok=True)
""";

    private const string ProbeStatusScript = """
from pathlib import Path
import json
import urllib.error
import urllib.request

path = Path.home() / ".cache" / "huggingface" / "token"
result = {
    "configured": False,
    "valid": None,
    "userName": None,
    "error": None,
    "tokenSource": str(path),
}

try:
    token = path.read_text(encoding="utf-8").strip() if path.exists() else ""
except Exception as ex:
    result["error"] = f"Token read failed: {ex}"
    print(json.dumps(result))
    raise SystemExit(0)

if not token:
    print(json.dumps(result))
    raise SystemExit(0)

result["configured"] = True
request = urllib.request.Request(
    "https://huggingface.co/api/whoami-v2",
    headers={
        "Authorization": f"Bearer {token}",
        "User-Agent": "DwemerDistroLauncher",
    },
)

try:
    with urllib.request.urlopen(request, timeout=12) as response:
        data = json.loads(response.read().decode("utf-8"))
    result["valid"] = True
    result["userName"] = data.get("name") or data.get("fullname")
except urllib.error.HTTPError as ex:
    result["valid"] = False if ex.code in (401, 403) else None
    result["error"] = "Token rejected by Hugging Face." if result["valid"] is False else f"Hugging Face returned HTTP {ex.code}."
except Exception as ex:
    result["valid"] = None
    result["error"] = str(ex)

print(json.dumps(result))
""";

    private sealed class ProbeResult
    {
        public bool Configured { get; set; }

        public bool? Valid { get; set; }

        public string? UserName { get; set; }

        public string? Error { get; set; }

        public string? TokenSource { get; set; }
    }
}

public sealed record HuggingFaceTokenStatus(
    bool IsConfigured,
    bool? IsValid,
    string? UserName,
    string? Error,
    string TokenSource)
{
    public static HuggingFaceTokenStatus Unknown(string error)
    {
        return new HuggingFaceTokenStatus(false, null, null, error, HuggingFaceTokenService.TokenPath);
    }
}
