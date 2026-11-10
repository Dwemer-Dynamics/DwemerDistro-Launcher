using System.Text;
using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// What the header belt shows for one service. Only Running is green, and it always means a verified answer;
/// the view picks each light from this state, never the other way round.
/// </summary>
public enum ComponentServiceState
{
    Checking,
    Running,
    Starting,
    Stopped,
    NotResponding,
    Disabled,
    Unverified,
    Unknown,
    DistroStopped,
    Paused,
}

/// <summary>
/// How the header belt can tell that a component's own service is the one answering. A port that is merely open
/// proves nothing about identity, so every kind without an identity check reports "open", never healthy.
/// </summary>
public enum ComponentHealthCheck
{
    /// <summary>Port open is all that can be said; no reliable identity or health endpoint is known.</summary>
    PortOnly,
    /// <summary>/provider_info or /openapi.json identifies the expected provider (the voice engine's rules).</summary>
    Provider,
    /// <summary>OmniVoice's GET /health answers status "ok".</summary>
    OmniVoiceHealth,
    /// <summary>Higgs's GET /v1/models lists higgs-v3 of family higgs_audio_tts (the voice engine's rule).</summary>
    HiggsModels,
    /// <summary>audio.cpp's health and model list identify the PocketTTS runtime, not model load state.</summary>
    AudioCppModels,
    /// <summary>LM Studio's own REST listing (GET /api/v0/models) answers. Model load state is never read from it.</summary>
    LmStudioApi,
}

/// <summary>
/// One installed runtime service the header belt watches: a catalog id, the Components card's label, artwork and
/// dedicated port, and how its health can be checked. Install-only tools without a port (CUDA) are not listed.
/// </summary>
public sealed record ComponentServiceDefinition(
    string Id,
    string Name,
    string Artwork,
    int Port,
    ComponentHealthCheck Check,
    string? Provider = null,
    string? PortFile = null,
    bool LegacyPort = false,
    string? EnabledMarker = null);

/// <summary>What one probe said about one service. Only ids, ports and closed-set words cross from the distro.</summary>
public sealed record ComponentServiceProbe(string Id, string Installed, bool? Enabled, int Port, string Health);

/// <summary>
/// The shared, read-only service status behind the header belt. It reuses the Components page's catalog rules for
/// the installed state and the voice engine's identity checks for health, and only ever reads: one bounded
/// Python run of fixed code, no command from the distro, no file content beyond a port number, no secret.
/// </summary>
public static class ComponentServiceStatus
{
    public const string ArtworkRoot = "pack://application:,,,/DwemerDistro;component/Assets/";

    /// <summary>Fixed belt order: the Components page's card order (TTS, STT, then services).</summary>
    public static readonly IReadOnlyList<ComponentServiceDefinition> Services =
    [
        new("audiocpp", "Pocket-TTS (GPU / audio.cpp)", "component-pocket-tts.png", 8086, ComponentHealthCheck.AudioCppModels),
        new("pockettts", "Pocket-TTS (CPU / Python)", "component-pocket-tts.png", 8024, ComponentHealthCheck.Provider, Provider: "pockettts",
            PortFile: "/home/dwemer/pocket-tts/.dwemerdistro-port", LegacyPort: true),
        new("higgs", "Higgs TTS 3", "component-higgs.png", 8025, ComponentHealthCheck.HiggsModels, EnabledMarker: "/home/dwemer/higgs-tts/.enabled"),
        new("chatterbox", "Chatterbox", "component-chatterbox.png", 8023, ComponentHealthCheck.Provider, Provider: "chatterbox",
            PortFile: "/home/dwemer/chatterbox/.dwemerdistro-port", LegacyPort: true),
        new("omnivoice", "Multilingual OmniVoice TTS", "component-omnivoice.png", 8021, ComponentHealthCheck.OmniVoiceHealth),
        new("xtts", "Dwemer Distro XTTS", "component-xtts.png", 8020, ComponentHealthCheck.Provider, Provider: "xtts"),
        new("pipertts", "Piper-TTS", "component-piper.png", 5000, ComponentHealthCheck.PortOnly),
        new("parakeet", "Parakeet STT", "nvidia.png", 8022, ComponentHealthCheck.PortOnly),
        new("localwhisper", "LocalWhisper", "component-whisper.png", 9876, ComponentHealthCheck.PortOnly),
        new("lmstudio", "LM Studio", "component-lmstudio.png", 1234, ComponentHealthCheck.LmStudioApi),
        new("minime", "Minime and TXT2VEC", "DwemerDistro.png", 8082, ComponentHealthCheck.PortOnly),
    ];

    private static readonly HashSet<string> InstalledWords = ["present", "absent", "unknown"];
    private static readonly HashSet<string> HealthWords = ["ok", "closed", "other", "open"];

    /// <summary>
    /// After the server reports ready, services that are enabled but not answering yet still count as starting
    /// for this long: model loads routinely outlast the shared startup script.
    /// </summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The fixed probe. Installed comes from the embedded catalog's rules exactly as the Components page computes
    /// it; enabled from the catalog startup rule (or Higgs's marker); health from loopback connects and the
    /// identity endpoints only. It prints one JSON line of ids, ports and closed-set words.
    /// </summary>
    public static string BuildProbeScript(string? componentId = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("python3 - <<'PY'");
        builder.AppendLine("from pathlib import Path");
        builder.AppendLine("from concurrent.futures import ThreadPoolExecutor");
        builder.AppendLine("import json");
        builder.AppendLine("import socket");
        builder.AppendLine("import urllib.request");
        builder.Append(DwemerDistro.Launcher.Wpf.ViewModels.InstallComponentsWindowViewModel.BuildCatalogProbeFunctions(ComponentCatalog.Embedded));
        builder.Append(ProbeFunctions);
        builder.AppendLine("SERVICES = [");
        foreach (var service in Services.Where(service => componentId is null || service.Id == componentId))
        {
            builder.Append("    (").Append(Py(service.Id)).Append(", ").Append(Py(service.Check.ToString()))
                .Append(", ").Append(service.Port)
                .Append(", ").Append(service.Provider is null ? "None" : Py(service.Provider))
                .Append(", ").Append(service.PortFile is null ? "None" : Py(service.PortFile))
                .Append(", ").Append(service.LegacyPort ? "True" : "False")
                .Append(", ").Append(service.EnabledMarker is null ? "None" : Py(service.EnabledMarker))
                .AppendLine("),");
        }

        builder.AppendLine("]");
        builder.AppendLine("with ThreadPoolExecutor(max_workers=6) as pool:");
        builder.AppendLine("    items = list(pool.map(lambda service: probe(*service), SERVICES))");
        builder.AppendLine("print(json.dumps({'items': items}))");
        builder.AppendLine("PY");
        return builder.ToString();
    }

    /// <summary>The probe's JSON, or null when it is not exactly the closed shape the script prints.</summary>
    public static IReadOnlyList<ComponentServiceProbe>? ParseProbe(string? output)
    {
        try
        {
            using var document = JsonDocument.Parse((output ?? string.Empty).Trim());
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            var known = Services.Select(service => service.Id).ToHashSet(StringComparer.Ordinal);
            var probes = new List<ComponentServiceProbe>();
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var installed = item.GetProperty("installed").GetString();
                var health = item.GetProperty("health").GetString();
                var port = item.GetProperty("port").GetInt32();
                var enabled = item.GetProperty("enabled");
                if (id is null || !known.Contains(id) || installed is null || !InstalledWords.Contains(installed)
                    || health is null || !HealthWords.Contains(health) || port is < 1 or > 65535
                    || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                {
                    return null;
                }

                probes.Add(new ComponentServiceProbe(id, installed, enabled.ValueKind == JsonValueKind.Null ? null : enabled.GetBoolean(), port, health));
            }

            return probes;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The belt state and its words for one probed service. Green needs a verified answer; an open port without an
    /// identity check stays unknown; a service that should be up and is not is red, unless startup is still in grace.
    /// </summary>
    public static (ComponentServiceState State, string Text) Describe(ComponentServiceDefinition service, ComponentServiceProbe probe, bool starting)
    {
        if (probe.Installed == "unknown")
        {
            return (ComponentServiceState.Unknown, "Installed state could not be confirmed.");
        }

        return probe.Health switch
        {
            "ok" => (ComponentServiceState.Running, service.Check == ComponentHealthCheck.LmStudioApi
                ? "Running. The LM Studio API is answering."
                : $"Running. {service.Name} answered its health check."),
            "open" => (ComponentServiceState.Unverified, service.Check == ComponentHealthCheck.PortOnly
                ? "Port is open. This service has no health check, so it is not confirmed running."
                : "Port is open, but the service could not be confirmed."),
            "other" when starting => (ComponentServiceState.Starting, "Starting. The port is open but the service is not answering yet."),
            "other" => (ComponentServiceState.NotResponding, $"Not responding. The port is open but did not answer as {service.Name}."),
            _ when probe.Enabled == false => (ComponentServiceState.Disabled, "Disabled at startup."),
            _ when starting => (ComponentServiceState.Starting, "Starting. Not answering yet."),
            _ when probe.Enabled == true => (ComponentServiceState.Stopped, "Stopped. Enabled at startup but not running."),
            _ => (ComponentServiceState.Unknown, "Not running. Its startup setting is not checked here."),
        };
    }

    private static string Py(string value) =>
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/')
            ? $"'{value}'"
            : throw new InvalidOperationException("A component service value is not a plain id or path.");

    // Identity checks repeat VoiceEngineService's identify_provider and Higgs rule, with shorter timeouts so one
    // slow service cannot hold the belt; the threads keep the whole run to a few seconds.
    private const string ProbeFunctions = """
        def port_open(port):
            try:
                with socket.create_connection(('127.0.0.1', port), timeout=0.4):
                    return True
            except OSError:
                return False

        def get_json(port, path):
            with urllib.request.urlopen(f'http://127.0.0.1:{port}{path}', timeout=1.5) as response:
                if response.status != 200:
                    raise ValueError('status')
                return json.loads(response.read(262144).decode('utf-8', errors='replace'))

        def identify_provider(port):
            try:
                provider = str(get_json(port, '/provider_info').get('provider') or '').strip().lower()
                if provider in ('pocket_tts', 'pocket-tts'):
                    provider = 'pockettts'
                if provider:
                    return provider
            except Exception:
                pass
            try:
                document = get_json(port, '/openapi.json')
                title = str((document.get('info') or {}).get('title') or '').strip().lower()
                paths = set((document.get('paths') or {}).keys())
                if '/languages' in paths or '/get_models_list' in paths:
                    return 'xtts'
                if 'chatterbox' in title or ({'/sample/{file_name}', '/speakers_list_extended'} <= paths):
                    return 'chatterbox'
                if '/tts_to_audio_form' in paths or ({'/tts_to_audio', '/voices/{voice_id}'} <= paths):
                    return 'pockettts'
            except Exception:
                pass
            return ''

        def saved_port(path, fallback):
            try:
                value = int(Path(path).read_text(encoding='utf-8').strip()[:6])
                return value if 1 <= value <= 65535 else fallback
            except (OSError, TypeError, ValueError):
                return fallback

        def verified(check, port):
            try:
                if check == 'OmniVoiceHealth':
                    return get_json(port, '/health').get('status') == 'ok'
                if check == 'HiggsModels':
                    return any(m.get('id') == 'higgs-v3' and m.get('family') == 'higgs_audio_tts' for m in get_json(port, '/v1/models').get('data', []))
                if check == 'AudioCppModels':
                    return get_json(port, '/health').get('status') == 'ok' and any(m.get('family') == 'pocket_tts' and m.get('task') == 'tts' for m in get_json(port, '/v1/models').get('data', []))
                if check == 'LmStudioApi':
                    return isinstance(get_json(port, '/api/v0/models').get('data'), list)
            except Exception:
                pass
            return False

        def probe(key, check, port, provider, port_file, legacy, marker):
            installed = CATALOG_STATE[key][0]
            if marker is not None:
                enabled = Path(marker).exists()
            elif CATALOG[key][1] is not None:
                enabled = startup_exists(key)
            else:
                enabled = None
            if check in ('HiggsModels', 'AudioCppModels'):
                try:
                    config = '/home/dwemer/audio.cpp/server.json' if check == 'AudioCppModels' else '/home/dwemer/higgs-tts/server.json'
                    candidate = int(json.loads(Path(config).read_text(encoding='utf-8'))['port'])
                    if 1 <= candidate <= 65535:
                        port = candidate
                except (OSError, ValueError, KeyError, TypeError):
                    pass
            result = {'id': key, 'installed': installed, 'enabled': enabled, 'port': port, 'health': 'closed'}
            if installed == 'absent':
                return result
            if check == 'Provider':
                # A saved port wins; without one only the dedicated port can be called wrong, so a legacy 8020
                # that another engine owns never turns this service red.
                configured = saved_port(port_file, port) if port_file else port
                candidates = []
                for candidate in (configured, port, 8020 if legacy else port):
                    if candidate not in candidates:
                        candidates.append(candidate)
                for candidate in candidates:
                    if port_open(candidate) and identify_provider(candidate) == provider:
                        result.update(port=candidate, health='ok')
                        return result
                result['port'] = configured
                if port_open(configured):
                    # Pocket-TTS on audio.cpp has no identity rule beyond the shared one, so its open port stays unconfirmed.
                    result['health'] = 'open' if key == 'audiocpp' else 'other'
                return result
            if not port_open(port):
                return result
            if check == 'PortOnly':
                result['health'] = 'open'
            elif verified(check, port):
                result['health'] = 'ok'
            else:
                result['health'] = 'open' if check == 'LmStudioApi' else 'other'
            return result

        """;
}
