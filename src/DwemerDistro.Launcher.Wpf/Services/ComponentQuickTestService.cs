using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed record ComponentTestResult(string Message, byte[]? Audio = null);

/// <summary>Explicit, bounded localhost sample requests. Nothing starts services or changes their settings.</summary>
public static class ComponentQuickTestService
{
    private const int MaxBytes = 10 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static bool IsSpeechInput(string id) => id is "parakeet" or "localwhisper";
    public static bool HasSample(string id) => id is "audiocpp" or "pockettts" or "higgs" or "chatterbox" or "xtts"
        or "pipertts" or "parakeet" or "localwhisper" or "lmstudio" or "minime" or "omnivoice";

    public static string Description(string id) => id switch
    {
        "parakeet" or "localwhisper" => "Choose a short WAV recording to transcribe. No microphone recording starts automatically.",
        "lmstudio" => "Send a short prompt to an already-loaded language model. No model will be downloaded or loaded by the launcher.",
        "minime" => "Run a short text classification through Minime. This tests Minime, not the separate Text2Vec service.",
        _ => "Generate a short speech sample, then use Play sample to hear it. The service uses its existing voice configuration."
    };

    // Reads at most 10 MB, including streamed responses, and never follows a service redirect outside localhost.
    private static async Task<byte[]> SendAsync(int port, string path, HttpContent? content, CancellationToken token)
    {
        if (port is < 1 or > 65535 || !path.StartsWith('/') || path.StartsWith("//")) throw new InvalidOperationException("Invalid local endpoint.");
        using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, $"http://127.0.0.1:{port}{path}") { Content = content };
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The service returned HTTP {(int)response.StatusCode}. Check that its model and voice are configured.");
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("The test response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new InvalidOperationException("The test response is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private static JsonElement Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    public static async Task<ComponentTestResult> RunSampleAsync(string id, int port, string text, string? audioFile, CancellationToken token)
    {
        if (!HasSample(id)) return new("This component supports a service check only. Generation was not tested.");
        if (text.Length > 300) throw new InvalidOperationException("Keep the test text under 300 characters.");
        if (IsSpeechInput(id))
        {
            if (string.IsNullOrEmpty(audioFile) || !File.Exists(audioFile)) throw new InvalidOperationException("Choose a short WAV file first.");
            var info = new FileInfo(audioFile);
            if (info.Length > MaxBytes || info.Length < 44) throw new InvalidOperationException("Choose a WAV recording smaller than 10 MB.");
            var bytes = await File.ReadAllBytesAsync(audioFile, token);
            if (!IsWave(bytes)) throw new InvalidOperationException("The selected file is not a WAV recording.");
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), id == "parakeet" ? "file" : "audio_file", "launcher-test.wav");
            var result = Parse(await SendAsync(port, id == "parakeet" ? "/v1/audio/transcriptions" : "/transcribe", form, token));
            if (!result.TryGetProperty("text", out var transcription) || transcription.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("The service did not return a transcription.");
            var transcript = transcription.GetString() ?? "";
            return new(string.IsNullOrWhiteSpace(transcript) ? "Request completed, but no speech was detected. Try a clearer recording." : "Transcription received:\n\n" + Limit(transcript));
        }
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Enter a short test message.");
        if (id == "lmstudio")
        {
            var listing = Parse(await SendAsync(port, "/api/v0/models", null, token));
            var model = listing.GetProperty("data").EnumerateArray().FirstOrDefault(m =>
                m.TryGetProperty("state", out var state) && state.GetString() == "loaded" &&
                m.TryGetProperty("type", out var type) && type.GetString() is "llm" or "vlm");
            if (model.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("No language model is loaded. Load one in LM Studio first.");
            var body = new { model = model.GetProperty("id").GetString(), messages = new[] { new { role = "user", content = text } }, max_tokens = 48, stream = false };
            var result = Parse(await SendAsync(port, "/v1/chat/completions", Json(body), token));
            var answer = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("The model returned no text.");
            return new("Model reply received:\n\n" + Limit(answer));
        }
        if (id == "minime")
        {
            var result = Parse(await SendAsync(port, "/extract?text=" + Uri.EscapeDataString(text), null, token));
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("is_memory_recall", out _) || result.TryGetProperty("error", out _))
                throw new InvalidOperationException("Minime did not return a classification result.");
            return new("Classification response received:\n\n" + Limit(result.ToString()));
        }

        byte[] audio;
        if (id is "audiocpp" or "higgs")
        {
            var listing = Parse(await SendAsync(port, "/v1/models", null, token));
            var family = id == "audiocpp" ? "pocket_tts" : "higgs_audio_tts";
            var model = listing.GetProperty("data").EnumerateArray().FirstOrDefault(m => m.TryGetProperty("family", out var value) && value.GetString() == family);
            if (model.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("The expected speech model is not configured.");
            // PocketTTS needs an explicit built-in voice when server.json has no default voice preset.
            object speechRequest = id == "audiocpp"
                ? new { model = model.GetProperty("id").GetString(), input = text, voice = "alba", response_format = "wav" }
                : new { model = model.GetProperty("id").GetString(), input = text, response_format = "wav" };
            audio = await SendAsync(port, "/v1/audio/speech", Json(speechRequest), token);
        }
        else if (id == "pipertts") audio = await SendAsync(port, "/", Json(new { text }), token);
        else if (id == "omnivoice") audio = await SendAsync(port, "/tts_to_audio", Json(new { text }), token);
        else
        {
            var speakers = Parse(await SendAsync(port, "/speakers_list", null, token));
            var voice = speakers.ValueKind == JsonValueKind.Array
                ? speakers.EnumerateArray().FirstOrDefault(v => v.ValueKind == JsonValueKind.String) : default;
            var speaker = voice.ValueKind == JsonValueKind.String ? voice.GetString() : null;
            if (string.IsNullOrWhiteSpace(speaker)) throw new InvalidOperationException("No voice is available. Configure a voice first.");
            object payload = id == "pockettts" ? new { text, speaker_wav = speaker }
                : new { text, speaker_wav = speaker, language = "en" };
            audio = await SendAsync(port, id == "chatterbox" ? "/tts_to_audio/" : "/tts_to_audio", Json(payload), token);
        }
        if (!IsWave(audio)) throw new InvalidOperationException("The service responded, but did not return WAV audio.");
        return new("Speech sample generated. Use Play sample to replay.", audio);
    }

    private static bool IsWave(byte[] bytes) => bytes.Length > 44 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE";
    private static string Limit(string text) => text.Length <= 2000 ? text : text[..2000] + "…";
}
