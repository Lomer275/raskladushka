namespace Raskladushka.Core.Dictation;

public sealed class VoiceServerRoute(TimeProvider time)
{
    public const string ServerUnavailableNotice = "Сервер недоступен — распознаю на этом компьютере.";
    public const string KeyRejectedNotice = "Ключ не подходит — распознаю на этом компьютере.";
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
    private long? _failedAt;

    public static TimeSpan TimeoutFor(TimeSpan audio) => TimeSpan.FromSeconds(3 + 0.1 * audio.TotalSeconds);
    public bool InCooldown => _failedAt is long stamp && time.GetElapsedTime(stamp) < Cooldown;

    public string? OnOutcome(RemoteOutcome outcome)
    {
        if (outcome == RemoteOutcome.Ok) return null;
        _failedAt = time.GetTimestamp();
        return outcome == RemoteOutcome.KeyRejected ? KeyRejectedNotice : ServerUnavailableNotice;
    }
}
