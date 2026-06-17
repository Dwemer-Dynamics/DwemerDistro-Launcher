using System.Text;
using System.Text.Json;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class HuggingFaceTokenService(WslService wsl)
{
    public const string TokenPath = "/home/dwemer/.cache/huggingface/token";

    public static readonly IReadOnlyList<HuggingFaceModelAccessDefinition> RequiredModelAccess = new[]
    {
        new HuggingFaceModelAccessDefinition(
            "pockettts",
            "Pocket-TTS voice cloning",
            "kyutai/pocket-tts",
            "https://huggingface.co/kyutai/pocket-tts"),
        new HuggingFaceModelAccessDefinition(
            "chatterbox",
            "Chatterbox",
            "ResembleAI/chatterbox-turbo",
            "https://huggingface.co/ResembleAI/chatterbox-turbo")
    };

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
            string.IsNullOrWhiteSpace(probe.TokenSource) ? TokenPath : probe.TokenSource,
            probe.Models?
                .Select(model => new HuggingFaceModelAccessStatus(
                    model.Key ?? string.Empty,
                    model.DisplayName ?? model.Key ?? "Model",
                    model.RepositoryId ?? string.Empty,
                    model.AccessUrl ?? string.Empty,
                    model.AccessStatus ?? "unknown",
                    model.Error))
                .ToArray() ?? []);
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

        var encodedToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalizedToken));
        return wsl.RunDistroAsUserAsync(
            LauncherConstants.DistroUser,
            new[] { "python3", "-c", SaveTokenScript, encodedToken },
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
import base64
import os
import sys

token = base64.b64decode(sys.argv[1]).decode("utf-8").strip()
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
    "models": [],
}

models = [
    {
        "key": "pockettts",
        "displayName": "Pocket-TTS voice cloning",
        "repositoryId": "kyutai/pocket-tts",
        "accessUrl": "https://huggingface.co/kyutai/pocket-tts",
        "probeUrl": "https://huggingface.co/kyutai/pocket-tts/resolve/main/languages/english/model.safetensors",
    },
    {
        "key": "chatterbox",
        "displayName": "Chatterbox",
        "repositoryId": "ResembleAI/chatterbox-turbo",
        "accessUrl": "https://huggingface.co/ResembleAI/chatterbox-turbo",
        "probeUrl": "https://huggingface.co/ResembleAI/chatterbox-turbo/resolve/main/tokenizer_config.json",
    },
]

def check_model_access(model, token, token_valid):
    if token and token_valid is False:
        return {
            "key": model["key"],
            "displayName": model["displayName"],
            "repositoryId": model["repositoryId"],
            "accessUrl": model["accessUrl"],
            "accessStatus": "invalid_token",
            "error": "The configured token is invalid.",
        }

    headers = {"User-Agent": "DwemerDistroLauncher"}
    if token:
        headers["Authorization"] = f"Bearer {token}"

    request = urllib.request.Request(model["probeUrl"], headers=headers, method="HEAD")
    try:
        with urllib.request.urlopen(request, timeout=12) as response:
            code = getattr(response, "status", 200)
        access_status = "granted" if 200 <= code < 400 else "unknown"
        error = None if access_status == "granted" else f"Hugging Face returned HTTP {code}."
    except urllib.error.HTTPError as ex:
        if ex.code in (401, 403):
            access_status = "needs_approval" if token else "token_required"
            error = "Open the model page, accept access, then click Refresh." if token else "A Hugging Face token is required for this model."
        elif ex.code == 404:
            access_status = "not_found"
            error = "Model file was not found or the repository is private."
        else:
            access_status = "unknown"
            error = f"Hugging Face returned HTTP {ex.code}."
    except Exception as ex:
        access_status = "unknown"
        error = str(ex)

    return {
        "key": model["key"],
        "displayName": model["displayName"],
        "repositoryId": model["repositoryId"],
        "accessUrl": model["accessUrl"],
        "accessStatus": access_status,
        "error": error,
    }

try:
    token = path.read_text(encoding="utf-8").strip() if path.exists() else ""
except Exception as ex:
    result["error"] = f"Token read failed: {ex}"
    print(json.dumps(result))
    raise SystemExit(0)

if not token:
    result["models"] = [check_model_access(model, token, None) for model in models]
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

result["models"] = [check_model_access(model, token, result["valid"]) for model in models]
print(json.dumps(result))
""";

    private sealed class ProbeResult
    {
        public bool Configured { get; set; }

        public bool? Valid { get; set; }

        public string? UserName { get; set; }

        public string? Error { get; set; }

        public string? TokenSource { get; set; }

        public List<ModelAccessProbeResult>? Models { get; set; }
    }

    private sealed class ModelAccessProbeResult
    {
        public string? Key { get; set; }

        public string? DisplayName { get; set; }

        public string? RepositoryId { get; set; }

        public string? AccessUrl { get; set; }

        public string? AccessStatus { get; set; }

        public string? Error { get; set; }
    }
}

public sealed record HuggingFaceModelAccessDefinition(
    string Key,
    string DisplayName,
    string RepositoryId,
    string AccessUrl);

public sealed record HuggingFaceModelAccessStatus(
    string Key,
    string DisplayName,
    string RepositoryId,
    string AccessUrl,
    string AccessStatus,
    string? Error);

public sealed record HuggingFaceTokenStatus(
    bool IsConfigured,
    bool? IsValid,
    string? UserName,
    string? Error,
    string TokenSource,
    IReadOnlyList<HuggingFaceModelAccessStatus> ModelAccess)
{
    public static HuggingFaceTokenStatus Unknown(string error)
    {
        return new HuggingFaceTokenStatus(false, null, null, error, HuggingFaceTokenService.TokenPath, []);
    }
}
