namespace Raskladushka.Core.Dictation;

public interface ISecretProtector
{
    string Protect(string plain);
    string? Unprotect(string protectedValue);
}

public static class VoiceServerAddress
{
    public static bool TryParse(string? text, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https")) return false;
        uri = parsed;
        return true;
    }
}

public enum RemoteOutcome { Ok, Unreachable, Busy, Timeout, KeyRejected, Failed }

public sealed record RemoteResult(RemoteOutcome Outcome, string? Text, int? Status, int UploadBytes, TimeSpan Elapsed);

public interface IRemoteTranscriber
{
    bool IsConfigured { get; }
    Task<RemoteResult> TranscribeAsync(AudioClip clip, string language, string prompt, TimeSpan timeout, CancellationToken ct);
}
