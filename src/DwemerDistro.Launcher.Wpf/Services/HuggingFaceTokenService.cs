using System.Text;
using System.Text.Json;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class HuggingFaceTokenService(WslService wsl)
{
    public const string TokenPath = "/home/dwemer/.cache/huggingface/token";

    public static bool HasManagedToken => !string.IsNullOrWhiteSpace(NormalizeToken(LauncherSecrets.ManagedHuggingFaceToken));

    public static readonly IReadOnlyList<HuggingFaceModelAccessDefinition> RequiredModelAccess = new[]
    {
        new HuggingFaceModelAccessDefinition(
            "pockettts",
            "Pocket-TTS voice cloning",
            "kyutai/pocket-tts",
            "https://huggingface.co/kyutai/pocket-tts"),
        new HuggingFaceModelAccessDefinition(
            "chatterbox",
            "Chatterbox voice cloning",
            "ResembleAI/chatterbox",
            "https://huggingface.co/ResembleAI/chatterbox"),
        new HuggingFaceModelAccessDefinition(
            "chatterbox-turbo",
            "Chatterbox Turbo voice cloning",
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

    public Task<bool> EnsureManagedTokenAsync(CancellationToken cancellationToken = default)
    {
        return EnsureManagedTokenAsync(overwriteExisting: false, cancellationToken);
    }

    public async Task<bool> EnsureManagedTokenAsync(bool overwriteExisting, CancellationToken cancellationToken = default)
    {
        if (!HasManagedToken)
        {
            return false;
        }

        var currentRawToken = await ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        var currentToken = NormalizeToken(currentRawToken);
        if (!overwriteExisting && !string.IsNullOrWhiteSpace(currentToken))
        {
            if (!string.Equals(currentRawToken, currentToken, StringComparison.Ordinal))
            {
                var sanitizeResult = await SaveTokenAsync(currentToken, cancellationToken).ConfigureAwait(false);
                return sanitizeResult.Succeeded;
            }

            return false;
        }

        var result = await SaveTokenAsync(NormalizeToken(LauncherSecrets.ManagedHuggingFaceToken), cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    public Task<CommandResult> SaveTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var normalizedToken = NormalizeToken(token);
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return ClearTokenAsync(cancellationToken);
        }

        var encodedToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalizedToken));
        return wsl.RunDistroAsUserWithEnvironmentAsync(
            LauncherConstants.DistroUser,
            new[] { "python3", "-c", SaveTokenScript },
            BuildTokenEnvironment(encodedToken),
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

    private static string NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        char[] hiddenCharacters = ['\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060'];
        var normalized = new StringBuilder(token.Trim());
        foreach (var character in hiddenCharacters)
        {
            normalized.Replace(character.ToString(), string.Empty);
        }

        return normalized.ToString().Trim();
    }

    private static Dictionary<string, string> BuildTokenEnvironment(string encodedToken)
    {
        const string tokenVariable = "DWEMER_HF_TOKEN_B64";
        var environment = new Dictionary<string, string>
        {
            [tokenVariable] = encodedToken,
            ["WSLENV"] = MergeWslEnv(Environment.GetEnvironmentVariable("WSLENV"), tokenVariable)
        };

        return environment;
    }

    private static string MergeWslEnv(string? currentWslEnv, string variableName)
    {
        if (string.IsNullOrWhiteSpace(currentWslEnv))
        {
            return variableName;
        }

        var parts = currentWslEnv
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!parts.Any(part => part.Split('/', 2)[0].Equals(variableName, StringComparison.Ordinal)))
        {
            parts.Add(variableName);
        }

        return string.Join(':', parts);
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

encoded = os.environ.get("DWEMER_HF_TOKEN_B64", "").strip()
if not encoded:
    print("Managed Hugging Face token was not passed to WSL.", file=sys.stderr)
    raise SystemExit(2)

try:
    token = "".join(
        ch for ch in base64.b64decode(encoded).decode("utf-8-sig").strip()
        if ch not in "\ufeff\u200b\u200c\u200d\u2060"
    )
except Exception as ex:
    print(f"Managed Hugging Face token could not be decoded: {ex}", file=sys.stderr)
    raise SystemExit(3)

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
        "displayName": "Chatterbox voice cloning",
        "repositoryId": "ResembleAI/chatterbox",
        "accessUrl": "https://huggingface.co/ResembleAI/chatterbox",
        "probeUrl": "https://huggingface.co/ResembleAI/chatterbox/resolve/main/ve.safetensors",
    },
    {
        "key": "chatterbox-turbo",
        "displayName": "Chatterbox Turbo voice cloning",
        "repositoryId": "ResembleAI/chatterbox-turbo",
        "accessUrl": "https://huggingface.co/ResembleAI/chatterbox-turbo",
        "probeUrl": "https://huggingface.co/ResembleAI/chatterbox-turbo/resolve/main/t3_turbo_v1.yaml",
    }
]

def normalize_token(value):
    return "".join(ch for ch in value.strip() if ch not in "\ufeff\u200b\u200c\u200d\u2060")

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
    raw_token = path.read_text(encoding="utf-8-sig", errors="replace") if path.exists() else ""
    token = normalize_token(raw_token)
    if token and raw_token.strip() != token:
        path.write_text(token + "\n", encoding="utf-8")
        path.chmod(0o600)
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
