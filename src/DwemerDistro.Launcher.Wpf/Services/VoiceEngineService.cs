using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class VoiceEngineService(WslService wsl)
{
    public async Task<VoiceEngineStatus> GetStatusAsync(
        SetupPreset preset,
        CancellationToken cancellationToken = default)
    {
        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", ProbeScript },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return VoiceEngineStatus.Unknown("Unable to check voice engines.");
        }

        Dictionary<string, bool>? installed;
        try
        {
            installed = JsonSerializer.Deserialize<Dictionary<string, bool>>(result.StandardOutput.Trim(), JsonOptions);
        }
        catch (JsonException)
        {
            installed = null;
        }

        if (installed is null)
        {
            return VoiceEngineStatus.Unknown("Unable to parse voice engine status.");
        }

        var preferredInstalled = installed.TryGetValue(preset.VoiceEngineKey, out var hasPreferred) && hasPreferred;
        var activeEngine = preferredInstalled
            ? preset.VoiceEngineKey
            : installed.FirstOrDefault(item => item.Value && IsClonedVoiceEngine(item.Key)).Key;

        if (string.IsNullOrWhiteSpace(activeEngine))
        {
            return new VoiceEngineStatus(
                false,
                preset.VoiceEngineKey,
                preset.VoiceEngineName,
                "No cloned voice engine detected yet.",
                installed);
        }

        var displayName = activeEngine == "pockettts"
                          && installed.TryGetValue("pockettts_audio_cpp", out var hasAudioCpp)
                          && hasAudioCpp
            ? "Pocket-TTS audio.cpp"
            : GetDisplayName(activeEngine);

        return new VoiceEngineStatus(
            true,
            activeEngine,
            displayName,
            $"{displayName} detected and ready to apply.",
            installed);
    }

    public async Task<IReadOnlyList<VoiceEngineApplyTargetStatus>> ApplyVoiceEngineAsync(
        string engineKey,
        CancellationToken cancellationToken = default)
    {
        await EnsurePostgresStartedAsync(cancellationToken).ConfigureAwait(false);
        var normalizedEngine = NormalizeEngineKey(engineKey);
        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", ApplyScript, normalizedEngine },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return
            [
                new VoiceEngineApplyTargetStatus(
                    "DwemerDistro",
                    string.Empty,
                    false,
                    false,
                    "Apply failed",
                    BuildCommandError(result))
            ];
        }

        try
        {
            var targets = JsonSerializer.Deserialize<List<VoiceEngineApplyTargetProbe>>(result.StandardOutput.Trim(), JsonOptions)
                          ?? [];
            return targets
                .Select(target => new VoiceEngineApplyTargetStatus(
                    target.TargetName ?? target.DatabaseName ?? "Unknown",
                    target.DatabaseName ?? string.Empty,
                    target.Applied,
                    target.Skipped,
                    target.StatusText ?? (target.Applied ? "Applied" : "Skipped"),
                    target.Error))
                .ToArray();
        }
        catch (JsonException)
        {
            return
            [
                new VoiceEngineApplyTargetStatus(
                    "DwemerDistro",
                    string.Empty,
                    false,
                    false,
                    "Apply failed",
                    "Unable to parse voice engine apply status.")
            ];
        }
    }

    public static string GetDisplayName(string engineKey)
    {
        return NormalizeEngineKey(engineKey) switch
        {
            "chatterbox" => "Chatterbox",
            "omnivoice" => "Multilingual OmniVoice",
            "pockettts" => "Pocket-TTS",
            _ => "Cloned voice engine"
        };
    }

    public static string NormalizeEngineKey(string engineKey)
    {
        return (engineKey ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "chatterbox" => "chatterbox",
            "omnivoice" or "omni_voice" or "omni-voice" => "omnivoice",
            "pocket_tts" or "pocket-tts" or "pockettts" => "pockettts",
            _ => "pockettts"
        };
    }

    private static bool IsClonedVoiceEngine(string key)
    {
        return NormalizeEngineKey(key) is "pockettts" or "chatterbox" or "omnivoice";
    }

    private async Task EnsurePostgresStartedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await wsl.RunDistroAsync(new[] { "service", "postgresql", "start" }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The apply probe below reports the actionable status.
        }
    }

    private static string BuildCommandError(Models.CommandResult result)
    {
        var text = (result.StandardError + result.StandardOutput).Trim();
        return string.IsNullOrWhiteSpace(text) ? $"Exit code {result.ExitCode}" : text;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string ProbeScript = """
from pathlib import Path
import json

audio_cpp = Path("/home/dwemer/audio.cpp/build/bin/audiocpp_server").is_file() and Path("/home/dwemer/audio.cpp/start.sh").exists()
python_pockettts = Path("/home/dwemer/pocket-tts/venv/bin/python").exists() and Path("/home/dwemer/pocket-tts/start.sh").exists()

status = {
    "chatterbox": Path("/home/dwemer/chatterbox/venv").exists(),
    "omnivoice": Path("/home/dwemer/omnivoice-tts/venv").exists(),
    "pockettts": audio_cpp or python_pockettts,
    "pockettts_audio_cpp": audio_cpp,
    "pockettts_python": python_pockettts,
}

print(json.dumps(status))
""";

    private const string ApplyScript = """"
import json
import os
import subprocess
import sys
import urllib.request
from pathlib import Path

engine = (sys.argv[1] if len(sys.argv) > 1 else "pockettts").strip().lower()
if engine not in ("pockettts", "chatterbox", "omnivoice"):
    engine = "pockettts"

def read_omnivoice_language():
    config_path = Path("/home/dwemer/omnivoice-tts/config.json")
    try:
        data = json.loads(config_path.read_text(encoding="utf-8"))
    except Exception:
        return "en"
    language = str(data.get("active_language") or "en").strip().lower()
    return language or "en"

active_language = read_omnivoice_language() if engine == "omnivoice" else "en"
def audio_cpp_available():
    if not (
        Path("/home/dwemer/audio.cpp/build/bin/audiocpp_server").is_file()
        and Path("/home/dwemer/audio.cpp/start.sh").exists()
    ):
        return False
    try:
        for path in ("/health", "/v1/models"):
            with urllib.request.urlopen("http://127.0.0.1:8086" + path, timeout=3) as response:
                json.loads(response.read().decode("utf-8", errors="replace"))
        return True
    except Exception:
        return False

pockettts_audio_cpp = engine == "pockettts" and audio_cpp_available()

def read_saved_port(path, fallback):
    try:
        value = int(Path(path).read_text(encoding="utf-8").strip())
        return value if 1 <= value <= 65535 else fallback
    except (OSError, TypeError, ValueError):
        return fallback

def identify_provider(port):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/provider_info", timeout=3) as response:
            data = json.loads(response.read().decode("utf-8", errors="replace"))
        provider = str(data.get("provider") or "").strip().lower()
        if provider in ("pocket_tts", "pocket-tts"):
            provider = "pockettts"
        if provider:
            return provider
    except Exception:
        pass
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/openapi.json", timeout=3) as response:
            document = json.loads(response.read().decode("utf-8", errors="replace"))
        title = str((document.get("info") or {}).get("title") or "").strip().lower()
        paths = set((document.get("paths") or {}).keys())
        if "/languages" in paths or "/get_models_list" in paths:
            return "xtts"
        if "chatterbox" in title or ({"/sample/{file_name}", "/speakers_list_extended"} <= paths):
            return "chatterbox"
        if "/tts_to_audio_form" in paths or ({"/tts_to_audio", "/voices/{voice_id}"} <= paths):
            return "pockettts"
    except Exception:
        pass
    return ""

def resolve_local_provider(expected, port_file, dedicated_port, legacy_port=8020):
    configured = read_saved_port(port_file, legacy_port)
    candidates = []
    for port in (configured, dedicated_port, legacy_port):
        if port not in candidates:
            candidates.append(port)
    for port in candidates:
        if identify_provider(port) == expected:
            return port, None
    return configured, f"{expected} is not running on configured, dedicated, or legacy ports"

if engine == "chatterbox":
    selected_port, service_error = resolve_local_provider(
        "chatterbox", "/home/dwemer/chatterbox/.dwemerdistro-port", 8023
    )
    herika_driver = "chatterbox"
    herika_label = "ddistro chatterbox"
    herika_url = f"http://127.0.0.1:{selected_port}"
    stobe_type = "chatterbox"
    stobe_name = "Chatterbox Default"
    stobe_url = herika_url
    display = "Chatterbox"
elif engine == "omnivoice":
    service_error = None if identify_provider(8021) == "omnivoice" else "omnivoice is not running on port 8021"
    herika_driver = "omnivoice"
    herika_label = "OmniVoice Default"
    herika_url = "http://127.0.0.1:8021"
    stobe_type = "omnivoice"
    stobe_name = "OmniVoice Default"
    stobe_url = "http://127.0.0.1:8021"
    display = "Multilingual OmniVoice"
else:
    selected_port, service_error = (8086, None) if pockettts_audio_cpp else resolve_local_provider(
        "pockettts", "/home/dwemer/pocket-tts/.dwemerdistro-port", 8024
    )
    herika_driver = "pockettts"
    herika_label = "Pocket TTS audio.cpp" if pockettts_audio_cpp else "ddistro pockettts"
    herika_url = f"http://127.0.0.1:{selected_port}"
    stobe_type = "pocket_tts"
    stobe_name = "Pocket TTS audio.cpp" if pockettts_audio_cpp else "Pocket TTS Default"
    stobe_url = herika_url
    display = "Pocket-TTS audio.cpp" if pockettts_audio_cpp else "Pocket-TTS"

TARGETS = [
    {"targetName": "CHIM / Skyrim", "databaseName": "dwemer"},
    {"targetName": "STOBE / Kenshi", "databaseName": "stobe"},
    {"targetName": "Dialectic / Fallout NV", "databaseName": "dialectic"},
]
database_override = os.environ.get("DWEMER_TTS_APPLY_DATABASES", "").split()
if database_override:
    TARGETS = [
        {"targetName": database_name, "databaseName": database_name}
        for database_name in database_override
    ]

def psql(db, sql):
    env = os.environ.copy()
    env["PGPASSWORD"] = env.get("PGPASSWORD") or "dwemer"
    return subprocess.run(
        ["psql", "-h", "127.0.0.1", "-U", "dwemer", "-d", db, "-At", "-c", sql],
        text=True,
        capture_output=True,
        timeout=25,
        env=env,
    )

def sql_literal(value):
    return "'" + str(value).replace("'", "''") + "'"

def columns_for(db, table_name):
    sql = "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = " + sql_literal(table_name) + ";"
    result = psql(db, sql)
    if result.returncode != 0:
        return None, (result.stderr or result.stdout).strip()
    return set(line.strip() for line in result.stdout.splitlines() if line.strip()), None

def apply_herika_style(db):
    driver = sql_literal(herika_driver)
    label = sql_literal(herika_label)
    url = sql_literal(herika_url)
    fallback_male = "default_male" if engine == "omnivoice" and db == "dialectic" else "malenord"
    fallback_female = "default_female" if engine == "omnivoice" and db == "dialectic" else "femalenord"
    if engine == "omnivoice":
        metadata = sql_literal(json.dumps({
            "language": active_language,
            "voicelogic": "voicetype",
            "fallback_male": fallback_male,
            "fallback_female": fallback_female,
        }))
    else:
        metadata_data = {
            "_title": display + " (DwemerDistro quickstart)",
            "voiceid": {"type": "string"},
            "language": {"type": "select", "values": [active_language]},
            "voicelogic": {"type": "select", "values": ["voicetype", "name"]},
            "fallback_male": {"type": "string", "default": fallback_male},
            "fallback_female": {"type": "string", "default": fallback_female},
        }
        if engine == "pockettts":
            metadata_data["api_format"] = "audio_cpp" if pockettts_audio_cpp else "legacy"
            if pockettts_audio_cpp:
                metadata_data["model"] = "pocket-tts"
        metadata = sql_literal(json.dumps(metadata_data))
    managed_labels = {
        "pockettts": ["ddistro pockettts", "pocket tts audio.cpp", "pocket tts default"],
        "chatterbox": ["ddistro chatterbox", "chatterbox default"],
        "omnivoice": ["omnivoice default", "ddistro omnivoice"],
    }.get(herika_driver, [herika_label])
    managed_labels_sql = ", ".join(sql_literal(value.lower()) for value in managed_labels)
    managed_match = f"driver = {driver} AND LOWER(label) IN ({managed_labels_sql})"
    connector_id = f"(SELECT id FROM core_tts_connector WHERE {managed_match} ORDER BY CASE WHEN LOWER(label) = LOWER({label}) THEN 0 ELSE 1 END, id LIMIT 1)"
    statements = [f"""
INSERT INTO core_tts_connector(driver, label, metadata, api_badge_id, url, voice_field)
SELECT {driver}, {label}, {metadata}::jsonb, NULL, {url}, 'voiceid'
WHERE NOT EXISTS (
    SELECT 1 FROM core_tts_connector WHERE {managed_match}
);

UPDATE core_tts_connector
SET label = {label},
    metadata = {metadata}::jsonb,
    url = {url},
    voice_field = 'voiceid'
WHERE id = {connector_id};
"""]

    profile_columns, _ = columns_for(db, "core_profiles")
    if profile_columns and "tts_connector_id" in profile_columns:
        profile_conditions = ["tts_connector_id IS NULL"]
        if "default_npc" in profile_columns:
            profile_conditions.append("COALESCE(default_npc, '') = '1'")
        if "default_narrator" in profile_columns:
            profile_conditions.append("COALESCE(default_narrator, '') = '1'")
        statements.append(f"""
UPDATE core_profiles
SET tts_connector_id = {connector_id}
WHERE {" OR ".join(profile_conditions)};
""")

    player_columns, _ = columns_for(db, "core_player")
    if player_columns and "tts_connector_id" in player_columns:
        player_conditions = ["tts_connector_id IS NULL"]
        if "id" in player_columns:
            player_conditions.append("id = 1")
        statements.append(f"""
UPDATE core_player
SET tts_connector_id = {connector_id}
WHERE {" OR ".join(player_conditions)};
""")

    sql = "\n".join(statements)
    return psql(db, sql)

def apply_stobe_style(db):
    provider = sql_literal(stobe_type)
    name = sql_literal(stobe_name)
    url = sql_literal(stobe_url)
    fallback_male = "default_male" if engine == "omnivoice" else "male1"
    fallback_female = "default_female" if engine == "omnivoice" else "female1"
    config_data = {
        "language": active_language,
        "fallback_male": fallback_male,
        "fallback_female": fallback_female,
        "stream_chunk_size": 20,
        "temperature": 0.9,
        "speed": 1.0,
        "length_penalty": 1.0,
        "repetition_penalty": 5.0,
        "top_p": 0.85,
        "top_k": 50,
        "enable_text_splitting": True,
    }
    if engine == "pockettts":
        config_data["api_format"] = "audio_cpp" if pockettts_audio_cpp else "legacy"
        if pockettts_audio_cpp:
            config_data["model"] = "pocket-tts"
    config = sql_literal(json.dumps(config_data))
    managed_names = {
        "pocket_tts": ["Pocket TTS Default", "Pocket TTS audio.cpp"],
        "chatterbox": ["Chatterbox Default"],
        "omnivoice": ["OmniVoice Default"],
    }.get(stobe_type, [stobe_name])
    managed_names_sql = ", ".join(sql_literal(value.lower()) for value in managed_names)
    match_clause = f"LOWER(name) IN ({managed_names_sql})"
    connector_id = f"(SELECT id FROM core_tts_connector WHERE {match_clause} ORDER BY CASE WHEN LOWER(name) = LOWER({name}) THEN 0 ELSE 1 END, id LIMIT 1)"
    statements = [f"""
UPDATE core_tts_connector
SET is_default = FALSE
WHERE connector_type IN ('pocket_tts', 'xtts', 'chatterbox', 'omnivoice', 'cartesia', 'inworld');

INSERT INTO core_tts_connector(name, connector_type, base_url, is_default, config)
SELECT {name}, {provider}, {url}, TRUE, {config}::jsonb
WHERE NOT EXISTS (
    SELECT 1 FROM core_tts_connector WHERE {match_clause}
);

UPDATE core_tts_connector
SET name = {name},
    connector_type = {provider},
    base_url = {url},
    is_default = TRUE,
    config = {config}::jsonb
WHERE id = {connector_id};
"""]

    profile_columns, _ = columns_for(db, "core_profiles")
    if profile_columns and "tts_connector_id" in profile_columns:
        profile_conditions = ["tts_connector_id IS NULL"]
        if "is_default_npc" in profile_columns:
            profile_conditions.append("COALESCE(is_default_npc, FALSE)")
        if "is_player_faction_profile" in profile_columns:
            profile_conditions.append("COALESCE(is_player_faction_profile, FALSE)")
        statements.append(f"""
UPDATE core_profiles
SET tts_connector_id = {connector_id}
WHERE {" OR ".join(profile_conditions)};
""")

    sql = "\n".join(statements)
    return psql(db, sql)

statuses = []
for target in TARGETS:
    db = target["databaseName"]
    if service_error:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": True,
            "statusText": "Voice service unavailable",
            "error": service_error,
        })
        continue
    columns, error = columns_for(db, "core_tts_connector")
    if error:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": True,
            "statusText": "Database unavailable",
            "error": error,
        })
        continue
    if not columns:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": True,
            "statusText": "TTS table not found",
            "error": None,
        })
        continue

    if "driver" in columns and "label" in columns:
        result = apply_herika_style(db)
    elif "connector_type" in columns and "name" in columns:
        result = apply_stobe_style(db)
    else:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": True,
            "statusText": "Unknown TTS schema",
            "error": None,
        })
        continue

    if result.returncode != 0:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": False,
            "skipped": False,
            "statusText": "Apply failed",
            "error": (result.stderr or result.stdout).strip(),
        })
    else:
        statuses.append({
            "targetName": target["targetName"],
            "databaseName": db,
            "applied": True,
            "skipped": False,
            "statusText": display + " applied",
            "error": None,
        })

print(json.dumps(statuses))
"""";

    private sealed class VoiceEngineApplyTargetProbe
    {
        public string? TargetName { get; set; }

        public string? DatabaseName { get; set; }

        public bool Applied { get; set; }

        public bool Skipped { get; set; }

        public string? StatusText { get; set; }

        public string? Error { get; set; }
    }
}

public sealed record VoiceEngineStatus(
    bool HasUsableEngine,
    string EngineKey,
    string DisplayName,
    string DetailText,
    IReadOnlyDictionary<string, bool> InstalledEngines)
{
    public static VoiceEngineStatus Unknown(string error)
    {
        return new VoiceEngineStatus(false, "pockettts", "Pocket-TTS", error, new Dictionary<string, bool>());
    }
}

public sealed record VoiceEngineApplyTargetStatus(
    string TargetName,
    string DatabaseName,
    bool Applied,
    bool Skipped,
    string StatusText,
    string? Error);
