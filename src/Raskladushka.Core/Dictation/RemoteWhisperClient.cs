using System.Net.Http.Headers;
using System.Text.Json;
using Raskladushka.Core.Config;

namespace Raskladushka.Core.Dictation;

public sealed class RemoteWhisperClient(HttpMessageHandler handler, Func<VoiceServerSettings> settings,
    ISecretProtector protector, TimeProvider time) : IRemoteTranscriber, IDisposable
{
    private readonly HttpClient _http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public bool IsConfigured => VoiceServerAddress.TryParse(settings().Url, out _);

    public async Task<RemoteResult> TranscribeAsync(AudioClip clip, string language, string prompt,
        TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = time.GetTimestamp();
        var uploadBytes = 0;
        int? status = null;
        RemoteResult Result(RemoteOutcome outcome, string? text = null) =>
            new(outcome, text, status, uploadBytes, time.GetElapsedTime(start));
        try
        {
            var current = settings();
            if (!VoiceServerAddress.TryParse(current.Url, out var uri)) return Result(RemoteOutcome.Failed);
            var key = string.IsNullOrEmpty(current.KeyProtected) ? null : protector.Unprotect(current.KeyProtected);
            if (!string.IsNullOrEmpty(current.KeyProtected) && key is null) return Result(RemoteOutcome.KeyRejected);
            var ogg = OpusOggEncoder.Encode(clip);
            uploadBytes = ogg.Length;
            ct.ThrowIfCancellationRequested();
            using var deadline = new CancellationTokenSource(timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(ogg);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
            form.Add(file, "file", "audio.ogg");
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent("0.0"), "temperature");
            form.Add(new StringContent(language), "language");
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
            request.Content = form;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            status = (int)response.StatusCode;
            if (status is 401 or 403) return Result(RemoteOutcome.KeyRejected);
            if (status is 429 or 503) return Result(RemoteOutcome.Busy);
            if (!response.IsSuccessStatusCode) return Result(RemoteOutcome.Failed);
            var body = await response.Content.ReadAsStringAsync(linked.Token);
            ct.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(text.GetString())) return Result(RemoteOutcome.Failed);
            return Result(RemoteOutcome.Ok, text.GetString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) { return Result(RemoteOutcome.Timeout); }
        catch (HttpRequestException) { ct.ThrowIfCancellationRequested(); return Result(RemoteOutcome.Unreachable); }
        catch (Exception) { ct.ThrowIfCancellationRequested(); return Result(RemoteOutcome.Failed); }
    }

    public void Dispose() => _http.Dispose();
}
