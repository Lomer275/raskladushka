namespace Raskladushka.Core.Voice;

public sealed class VoiceInstallException(VoiceFailure failure, string message) : Exception(message)
{
    public VoiceFailure Failure { get; } = failure;
}
