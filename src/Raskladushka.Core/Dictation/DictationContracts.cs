namespace Raskladushka.Core.Dictation;

public enum WhisperMode { Gpu, Cpu }
public sealed class WhisperStartException(string message) : Exception(message);

public enum MicrophoneProblem { NotFound, AccessDenied, Busy }
public sealed class MicrophoneException(MicrophoneProblem problem) : Exception(problem.ToString())
{
    public MicrophoneProblem Problem { get; } = problem;
}

public enum NotifyAction { None, OpenMicrophoneSettings, OpenVoiceSetup, OpenReleasePage }

public interface IAudioRecorder
{
    void Start();
    AudioClip Stop();
    void Cancel();
}

public interface IWhisperHost
{
    bool IsRunning { get; }
    bool HasGpuBuild { get; }
    bool HasCpuModel { get; }
    Task StartAsync(WhisperMode mode, CancellationToken ct);
    Task<string> TranscribeAsync(AudioClip clip, string language, string prompt, CancellationToken ct);
    void Stop();
}

public interface ITextInserter
{
    Task InsertAsync(string text);
}

public enum IndicatorState { Hidden, Recording, LoadingModel, Transcribing }

public interface IDictationUi
{
    void SetIndicator(IndicatorState state);
    void Notify(string message, NotifyAction action = NotifyAction.None);
}

public enum DictationState { Idle, Recording, Processing }
