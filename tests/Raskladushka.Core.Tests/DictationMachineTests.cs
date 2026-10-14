using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;
using Microsoft.Extensions.Time.Testing;

namespace Raskladushka.Core.Tests;

public sealed class DictationMachineTests
{
    private const string CpuNotice = "Видеокарта не отвечает — распознаю на процессоре, это медленнее.";
    private const string StartNotice = "Whisper не запустился. Подробности в журнале.";
    private const string SpeechNotice = "Не удалось распознать речь. Подробности в журнале.";

    [Fact]
    public void DownStartsRecordingAndGpuOnce()
    {
        var f = new Fixture();
        f.Machine.OnHotkeyDown();
        f.Machine.OnHotkeyDown();
        f.Machine.OnHotkeyDown();
        Assert.Equal(1, f.Recorder.Starts);
        Assert.Equal([WhisperMode.Gpu], f.Host.Starts);
        Assert.Equal(DictationState.Recording, f.Machine.State);
        Assert.Equal(IndicatorState.Recording, f.Ui.Indicators.Last());
    }

    [Fact]
    public async Task DeadHostRestartsOnGpuBetweenDictations()
    {
        var f = new Fixture();
        await f.Dictate();
        var oldToken = f.Host.StartToken;
        f.Host.IsRunning = false;
        f.Machine.OnHotkeyDown();
        Assert.Equal([WhisperMode.Gpu, WhisperMode.Gpu], f.Host.Starts);
        Assert.True(oldToken.IsCancellationRequested);
        await f.Machine.OnHotkeyUpAsync();
        Assert.Equal(2, f.Inserter.Texts.Count);
        Assert.Empty(f.Ui.Notices);
        f.AssertIdle();
    }

    [Fact]
    public async Task FailedStartupAfterCancellationRetriesGpu()
    {
        var f = new Fixture();
        f.Host.FailGpuStart = true;
        f.Host.FailCpuStart = true;
        f.Machine.OnHotkeyDown();
        var oldToken = f.Host.StartToken;
        Assert.Equal([WhisperMode.Gpu, WhisperMode.Cpu], f.Host.Starts);
        f.Machine.OnOtherKeyWhileHeld();
        f.Host.FailGpuStart = false;
        f.Host.FailCpuStart = false;
        f.Machine.OnHotkeyDown();
        Assert.Equal([WhisperMode.Gpu, WhisperMode.Cpu, WhisperMode.Gpu], f.Host.Starts);
        Assert.True(oldToken.IsCancellationRequested);
        await f.Machine.OnHotkeyUpAsync();
        Assert.Single(f.Inserter.Texts);
        Assert.Equal([CpuNotice], f.Ui.Notices);
        f.AssertIdle();
    }

    [Fact]
    public async Task PendingStartupAfterCancellationIsReused()
    {
        var f = new Fixture();
        f.Host.GpuGate = new();
        f.Machine.OnHotkeyDown();
        var token = f.Host.StartToken;
        f.Machine.OnOtherKeyWhileHeld();
        f.Machine.OnHotkeyDown();
        Assert.Equal([WhisperMode.Gpu], f.Host.Starts);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(2, f.Recorder.Starts);
        f.Host.GpuGate.SetResult();
        await f.Machine.OnHotkeyUpAsync();
        Assert.Single(f.Inserter.Texts);
        f.AssertIdle();
    }

    [Fact]
    public void CancellationKeepsStartupUntilIdleUnload()
    {
        var f = new Fixture();
        f.Host.GpuGate = new();
        f.Machine.OnHotkeyDown();
        f.Machine.OnOtherKeyWhileHeld();
        f.Machine.OnOtherKeyWhileHeld();
        Assert.Equal(1, f.Recorder.Cancels);
        f.AssertIdle();
        Assert.Empty(f.Host.Clips);
        Assert.False(f.Host.StartToken.IsCancellationRequested);
        f.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, f.Host.Stops);
        Assert.True(f.Host.StartToken.IsCancellationRequested);
        f.Host.GpuGate.SetResult();
    }

    [Theory]
    [InlineData(0.2, 0.1)]
    [InlineData(2, 0.001)]
    public async Task ShortOrSilentClipIsDiscarded(double seconds, double rms)
    {
        var f = new Fixture();
        f.Recorder.Clip = new([], TimeSpan.FromSeconds(seconds), rms);
        await f.Dictate();
        Assert.Empty(f.Host.Clips);
        Assert.Empty(f.Inserter.Texts);
        f.AssertIdle();
    }

    [Fact]
    public async Task PendingStartupThenTranscriptionAndCleanInsertion()
    {
        var f = new Fixture();
        f.Settings = new() { Language = "en", Prompt = "terms" };
        f.Host.GpuGate = new();
        f.Host.TranscriptionGate = new();
        f.Host.Raw = "первая строка\nВторая строка";
        f.Machine.OnHotkeyDown();
        var up = f.Machine.OnHotkeyUpAsync();
        Assert.Equal(IndicatorState.LoadingModel, f.Ui.Indicators.Last());
        f.Machine.OnHotkeyDown();
        f.Machine.OnOtherKeyWhileHeld();
        Assert.Equal(1, f.Recorder.Starts);
        Assert.Equal(0, f.Recorder.Cancels);
        f.Host.GpuGate.SetResult();
        await f.Host.TranscriptionEntered.Task;
        Assert.Equal(IndicatorState.Transcribing, f.Ui.Indicators.Last());
        f.Host.TranscriptionGate.SetResult();
        await up;
        Assert.Equal(["первая строка. Вторая строка "], f.Inserter.Texts);
        Assert.Same(f.Recorder.Clip, Assert.Single(f.Host.Clips));
        Assert.Equal(("en", "terms"), f.Host.Parameters);
        f.AssertIdle();
    }

    [Fact]
    public async Task HallucinationIsNotInserted()
    {
        var f = new Fixture();
        f.Host.Raw = "Продолжение следует...";
        await f.Dictate();
        Assert.Empty(f.Inserter.Texts);
        f.AssertIdle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GpuFailureFallsBackOnceAndCpuPersists(bool startup)
    {
        var f = new Fixture();
        f.Host.FailGpuStart = startup;
        f.Host.FailGpuTranscription = !startup;
        await f.Dictate();
        Assert.Equal([WhisperMode.Gpu, WhisperMode.Cpu], f.Host.Starts);
        Assert.Equal(1, f.Host.Stops);
        Assert.Equal([CpuNotice], f.Ui.Notices);
        Assert.Single(f.Inserter.Texts);
        await f.Dictate();
        Assert.Equal(2, f.Host.Starts.Count);
        Assert.Single(f.Ui.Notices);
        Assert.Equal(2, f.Inserter.Texts.Count);
    }

    [Fact]
    public async Task CpuTranscriptionFailureIsContained()
    {
        var f = new Fixture();
        f.Host.FailGpuTranscription = true;
        f.Host.FailCpuTranscription = true;
        await f.Dictate();
        Assert.Equal([CpuNotice, SpeechNotice], f.Ui.Notices);
        Assert.Equal(2, f.Host.Clips.Count);
        Assert.Empty(f.Inserter.Texts);
        f.AssertIdle();
    }

    [Fact]
    public async Task BothStartupFailuresAreContained()
    {
        var f = new Fixture();
        f.Host.FailGpuStart = true;
        f.Host.FailCpuStart = true;
        await f.Dictate();
        Assert.Equal([CpuNotice, StartNotice], f.Ui.Notices);
        f.AssertIdle();
    }

    [Fact]
    public async Task StartupDeadlineBeginsAtRelease()
    {
        var f = new Fixture();
        f.Host.GpuGate = new();
        f.Machine.OnHotkeyDown();
        f.Time.Advance(TimeSpan.FromSeconds(60));
        var up = f.Machine.OnHotkeyUpAsync();
        f.Time.Advance(TimeSpan.FromSeconds(89));
        Assert.False(up.IsCompleted);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await up;
        Assert.Equal([StartNotice], f.Ui.Notices);
        f.AssertIdle();
        f.Host.GpuGate.SetResult();
    }

    [Fact]
    public async Task CpuStartupUsesRemainingReleaseDeadline()
    {
        var f = new Fixture();
        f.Host.GpuGate = new();
        f.Host.CpuGate = new();
        f.Machine.OnHotkeyDown();
        var up = f.Machine.OnHotkeyUpAsync();
        f.Time.Advance(TimeSpan.FromSeconds(80));
        f.Host.GpuGate.SetException(new WhisperStartException("секретный сбой"));
        await f.Host.CpuStartEntered.Task;
        Assert.Equal([WhisperMode.Gpu, WhisperMode.Cpu], f.Host.Starts);
        f.Time.Advance(TimeSpan.FromSeconds(10));
        await up;
        Assert.Equal([CpuNotice, StartNotice], f.Ui.Notices);
        f.AssertIdle();
        f.Host.CpuGate.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecorderStopAndCancelFailuresAreContained(bool cancel)
    {
        var f = new Fixture();
        f.Recorder.FailStop = true;
        f.Recorder.FailCancel = true;
        f.Machine.OnHotkeyDown();
        if (cancel) f.Machine.OnOtherKeyWhileHeld();
        else await f.Machine.OnHotkeyUpAsync();
        f.AssertIdle();
        Assert.Empty(f.Host.Clips);
        Assert.All(f.Log.Concat(f.Ui.Notices), entry => Assert.DoesNotContain("секретный", entry));
    }

    [Fact]
    public async Task RunningHostIsReused()
    {
        var f = new Fixture();
        await f.Host.StartAsync(WhisperMode.Gpu, CancellationToken.None);
        await f.Dictate();
        Assert.Single(f.Host.Starts);
        Assert.Single(f.Inserter.Texts);
    }

    [Fact]
    public async Task IdleStopsHostOnce()
    {
        var f = new Fixture();
        await f.Dictate();
        f.Time.Advance(TimeSpan.FromMinutes(5));
        f.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, f.Host.Stops);
    }

    [Fact]
    public async Task NewRecordingCancelsIdleAndCompletionRestartsIt()
    {
        var f = new Fixture();
        await f.Dictate();
        f.Time.Advance(TimeSpan.FromMinutes(4));
        f.Machine.OnHotkeyDown();
        f.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, f.Host.Stops);
        await f.Machine.OnHotkeyUpAsync();
        f.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, f.Host.Stops);
        f.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, f.Host.Stops);
    }

    [Fact]
    public async Task IdleUnloadResetsCpuMode()
    {
        var f = new Fixture();
        f.Host.FailGpuStart = true;
        await f.Dictate();
        f.Time.Advance(TimeSpan.FromMinutes(5));
        f.Host.FailGpuStart = false;
        await f.Dictate();
        Assert.Equal([WhisperMode.Gpu, WhisperMode.Cpu, WhisperMode.Gpu], f.Host.Starts);
    }

    [Fact]
    public async Task RecordingLimitProcessesOnce()
    {
        var f = new Fixture();
        f.Machine.OnHotkeyDown();
        f.Time.Advance(TimeSpan.FromSeconds(120));
        await f.Inserter.Inserted.Task;
        await f.Machine.OnHotkeyUpAsync();
        Assert.Equal(1, f.Recorder.Stops);
        Assert.Single(f.Host.Clips);
        Assert.Single(f.Inserter.Texts);
        f.AssertIdle();
    }

    [Fact]
    public void UnavailableMicrophoneIsContained()
    {
        var f = new Fixture();
        f.Recorder.FailStart = true;
        f.Machine.OnHotkeyDown();
        Assert.Equal(["Микрофон недоступен."], f.Ui.Notices);
        f.AssertIdle();
    }

    [Fact]
    public async Task UnmatchedReleaseDoesNothing()
    {
        var f = new Fixture();
        await f.Machine.OnHotkeyUpAsync();
        f.Machine.OnHotkeyDown();
        f.Machine.OnOtherKeyWhileHeld();
        await f.Machine.OnHotkeyUpAsync();
        Assert.Equal(0, f.Recorder.Stops);
        Assert.Empty(f.Host.Clips);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogsAndNoticesNeverContainTranscriptOrInsertionException(bool failInsert)
    {
        var f = new Fixture();
        f.Host.Raw = "секретный текст";
        f.Inserter.Fail = failInsert;
        await f.Dictate();
        Assert.NotEmpty(f.Log);
        Assert.All(f.Log.Concat(f.Ui.Notices), entry => Assert.DoesNotContain("секретный", entry));
        Assert.Contains(f.Log, entry => entry.Contains("2"));
        if (failInsert) Assert.Contains(f.Log, entry => entry.Contains("failed"));
        else Assert.Equal(["секретный текст "], f.Inserter.Texts);
        f.AssertIdle();
    }

    [Theory]
    [InlineData(false, "Gpu")]
    [InlineData(true, "Cpu")]
    public async Task StartupLogIncludesModeAndElapsedTime(bool cpu, string mode)
    {
        var f = new Fixture();
        f.Host.FailGpuStart = cpu;
        var gate = new TaskCompletionSource();
        if (cpu) f.Host.CpuGate = gate;
        else f.Host.GpuGate = gate;
        f.Machine.OnHotkeyDown();
        f.Time.Advance(TimeSpan.FromSeconds(2.5));
        gate.SetResult();
        await f.Machine.OnHotkeyUpAsync();
        Assert.Contains($"Whisper ready; mode={mode}; startup: {2.5:F3} s.", f.Log);
    }

    [Fact]
    public async Task StartupFailureLogsExceptionTypeAndSafeStartupMessage()
    {
        var f = new Fixture();
        f.Host.FailGpuStart = true;
        f.Host.FailCpuStart = true;
        await f.Dictate();
        Assert.Contains("Whisper startup attempt failed: WhisperStartException. секретный сбой", f.Log);
        Assert.Contains("Whisper start failed: WhisperStartException. секретный сбой", f.Log);
    }

    [Theory]
    [InlineData(false, "Transcription")]
    [InlineData(true, "Insertion")]
    public async Task RuntimeFailureLogsTypeButNeverExceptionMessage(bool inserting, string operation)
    {
        var f = new Fixture();
        f.Inserter.Fail = inserting;
        f.Host.FailGpuTranscription = !inserting;
        f.Host.FailCpuTranscription = !inserting;
        await f.Dictate();
        Assert.Contains($"{operation} failed: InvalidOperationException.", f.Log);
        Assert.All(f.Log, line => Assert.DoesNotContain("секретный", line));
    }

    [Fact]
    public async Task StartupTimeoutLogsExceptionType()
    {
        var f = new Fixture();
        f.Host.GpuGate = new();
        f.Machine.OnHotkeyDown();
        var release = f.Machine.OnHotkeyUpAsync();
        f.Time.Advance(DictationMachine.StartTimeout);
        await release;
        Assert.Contains("Whisper start failed: TimeoutException.", f.Log);
        f.Host.GpuGate.TrySetResult();
    }

    [Fact]
    public async Task MissingGpuStartsCpuWithoutNotice()
    {
        var f = new Fixture();
        f.Host.HasGpuBuild = false;
        await f.Dictate();
        Assert.Equal([WhisperMode.Cpu], f.Host.Starts);
        Assert.Empty(f.Ui.Notices);
        Assert.Equal(["привет "], f.Inserter.Texts);
        Assert.Contains("Whisper GPU build not installed; starting on CPU.", f.Log);
        f.AssertIdle();
    }

    [Fact]
    public async Task MissingCpuModelExplainsSlowFallback()
    {
        var f = new Fixture();
        f.Host.FailGpuStart = true;
        f.Host.HasCpuModel = false;
        await f.Dictate();
        Assert.Equal([WhisperMode.Gpu, WhisperMode.Cpu], f.Host.Starts);
        Assert.Equal(["Видеокарта не отвечает — распознаю на процессоре, это очень медленно. Быстрее станет, если скачать модель для процессора: меню значка → «Голосовой ввод…»."], f.Ui.Notices);
    }

    [Fact]
    public async Task DirectCpuStartupFailureIsContained()
    {
        var f = new Fixture();
        f.Host.HasGpuBuild = false;
        f.Host.FailCpuStart = true;
        await f.Dictate();
        Assert.Equal([WhisperMode.Cpu], f.Host.Starts);
        Assert.Equal([StartNotice], f.Ui.Notices);
        f.AssertIdle();
    }

    [Fact]
    public async Task GpuInstalledDuringIdleIsUsedOnNextStart()
    {
        var f = new Fixture();
        f.Host.HasGpuBuild = false;
        await f.Dictate();
        f.Time.Advance(TimeSpan.FromMinutes(5));
        f.Host.HasGpuBuild = true;
        await f.Dictate();
        Assert.Equal([WhisperMode.Cpu, WhisperMode.Gpu], f.Host.Starts);
        Assert.Empty(f.Ui.Notices);
        Assert.Equal(2, f.Inserter.Texts.Count);
    }

    [Theory]
    [InlineData(MicrophoneProblem.NotFound, "Микрофон не найден. Подключите микрофон и попробуйте снова.", NotifyAction.None)]
    [InlineData(MicrophoneProblem.AccessDenied, "Windows не даёт программам доступ к микрофону. Включите: Параметры → Конфиденциальность → Микрофон → «Разрешить классическим приложениям доступ к микрофону».", NotifyAction.OpenMicrophoneSettings)]
    [InlineData(MicrophoneProblem.Busy, "Микрофон недоступен.", NotifyAction.None)]
    [InlineData(null, "Микрофон недоступен.", NotifyAction.None)]
    public void MicrophoneStartProblemIsExplained(MicrophoneProblem? problem, string notice, NotifyAction action)
    {
        var f = new Fixture();
        f.Recorder.StartError = problem.HasValue ? new MicrophoneException(problem.Value) : new InvalidOperationException();
        f.Machine.OnHotkeyDown();
        Assert.Equal([notice], f.Ui.Notices);
        Assert.Equal([action], f.Ui.Actions);
        Assert.Contains($"Microphone start failed: {problem?.ToString() ?? "other"}.", f.Log);
        f.AssertIdle();
    }

    [Theory]
    [InlineData(2, 0, true)]
    [InlineData(2, 0.001, false)]
    [InlineData(0.2, 0, false)]
    public async Task DigitalSilenceIsExplainedWithoutWaitingForStartup(double seconds, double rms, bool digital)
    {
        var f = new Fixture();
        f.Host.GpuGate = new();
        f.Recorder.Clip = new([], TimeSpan.FromSeconds(seconds), rms);
        await f.Dictate();
        Assert.Empty(f.Host.Clips);
        Assert.Empty(f.Inserter.Texts);
        if (digital)
        {
            Assert.Equal(["Микрофон передаёт тишину. Проверьте, что он включён и что Windows разрешает доступ: Параметры → Конфиденциальность → Микрофон."], f.Ui.Notices);
            Assert.Equal([NotifyAction.OpenMicrophoneSettings], f.Ui.Actions);
            Assert.Contains("Recording is digital silence.", f.Log);
        }
        else
        {
            Assert.Empty(f.Ui.Notices);
            Assert.Contains("Recording discarded.", f.Log);
        }
        f.AssertIdle();
        f.Host.GpuGate.SetResult();
    }

    [Fact]
    public async Task SuspensionStopsHostAndResumeAllowsDictation()
    {
        var f = new Fixture();
        await f.Dictate();
        f.Machine.Suspend();
        f.Machine.Suspend();
        Assert.Equal(1, f.Host.Stops);
        await f.Dictate();
        Assert.Equal(1, f.Recorder.Starts);
        Assert.Empty(f.Ui.Notices);
        f.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, f.Host.Stops);
        f.Machine.Resume();
        f.Machine.Resume();
        await f.Dictate();
        Assert.Equal(2, f.Inserter.Texts.Count);
        Assert.Equal([WhisperMode.Gpu, WhisperMode.Gpu], f.Host.Starts);
        Assert.Single(f.Log, line => line == "Dictation suspended.");
        Assert.Single(f.Log, line => line == "Dictation resumed.");
        f.AssertIdle();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SuspendedTranscriptionIsDropped(bool fail, bool resumeBeforeCompletion)
    {
        var f = new Fixture();
        f.Host.Raw = "секретный текст";
        f.Host.FailGpuTranscription = fail;
        f.Host.TranscriptionGate = new();
        f.Machine.OnHotkeyDown();
        var release = f.Machine.OnHotkeyUpAsync();
        await f.Host.TranscriptionEntered.Task;
        f.Machine.Suspend();
        if (resumeBeforeCompletion) f.Machine.Resume();
        f.Host.TranscriptionGate.SetResult();
        await release;
        Assert.Equal([WhisperMode.Gpu], f.Host.Starts);
        Assert.Empty(f.Inserter.Texts);
        Assert.Empty(f.Ui.Notices);
        Assert.Contains("Dictation result dropped: suspended.", f.Log);
        Assert.All(f.Log, line => Assert.DoesNotContain("секретный", line));
        f.AssertIdle();
    }

    [Fact]
    public async Task SuspensionCancelsRecording()
    {
        var f = new Fixture();
        f.Machine.OnHotkeyDown();
        f.Machine.Suspend();
        Assert.Equal(1, f.Recorder.Cancels);
        await f.Machine.OnHotkeyUpAsync();
        f.Time.Advance(DictationMachine.MaxRecording);
        Assert.Empty(f.Host.Clips);
        Assert.Empty(f.Inserter.Texts);
        f.AssertIdle();
    }

    [Fact]
    public async Task SuspensionDuringStartupDoesNotFallback()
    {
        var f = new Fixture();
        f.Host.GpuGate = new();
        f.Machine.OnHotkeyDown();
        var release = f.Machine.OnHotkeyUpAsync();
        f.Machine.Suspend();
        f.Host.GpuGate.SetResult();
        await release;
        Assert.Equal([WhisperMode.Gpu], f.Host.Starts);
        Assert.Empty(f.Host.Clips);
        Assert.Empty(f.Ui.Notices);
        Assert.Contains("Dictation result dropped: suspended.", f.Log);
        f.AssertIdle();
    }

    [Fact]
    public async Task MicrophoneStopProblemKeepsGenericNotice()
    {
        var f = new Fixture();
        f.Recorder.StopError = new MicrophoneException(MicrophoneProblem.AccessDenied);
        await f.Dictate();
        Assert.Equal(["Микрофон недоступен."], f.Ui.Notices);
        Assert.Equal([NotifyAction.None], f.Ui.Actions);
        f.AssertIdle();
    }

    private sealed class Fixture
    {
        public FakeTimeProvider Time { get; } = new();
        public Recorder Recorder { get; } = new();
        public Host Host { get; } = new();
        public Inserter Inserter { get; } = new();
        public Ui Ui { get; } = new();
        public List<string> Log { get; } = [];
        public DictationSettings Settings { get; set; } = new();
        public DictationMachine Machine { get; }
        public Fixture() => Machine = new(Recorder, Host, Inserter, Ui, Time, () => Settings, Log.Add);
        public async Task Dictate()
        {
            Machine.OnHotkeyDown();
            await Machine.OnHotkeyUpAsync();
        }
        public void AssertIdle()
        {
            Assert.Equal(DictationState.Idle, Machine.State);
            Assert.Equal(IndicatorState.Hidden, Ui.Indicators.Last());
        }
    }

    private sealed class Recorder : IAudioRecorder
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Cancels { get; private set; }
        public bool FailStart { get; set; }
        public Exception? StartError { get; set; }
        public Exception? StopError { get; set; }
        public bool FailStop { get; set; }
        public bool FailCancel { get; set; }
        public AudioClip Clip { get; set; } = new([1, 2], TimeSpan.FromSeconds(2), 0.1);
        public void Start()
        {
            Starts++;
            if (StartError is not null) throw StartError;
            if (FailStart) throw new InvalidOperationException("microphone");
        }
        public AudioClip Stop()
        {
            Stops++;
            if (StopError is not null) throw StopError;
            if (FailStop) throw new InvalidOperationException("секретный сбой");
            return Clip;
        }
        public void Cancel()
        {
            Cancels++;
            if (FailCancel) throw new InvalidOperationException("секретный сбой");
        }
    }

    private sealed class Host : IWhisperHost
    {
        public bool IsRunning { get; set; }
        public bool HasGpuBuild { get; set; } = true;
        public bool HasCpuModel { get; set; } = true;
        public List<WhisperMode> Starts { get; } = [];
        public List<AudioClip> Clips { get; } = [];
        public int Stops { get; private set; }
        public bool FailGpuStart { get; set; }
        public bool FailCpuStart { get; set; }
        public bool FailGpuTranscription { get; set; }
        public bool FailCpuTranscription { get; set; }
        public TaskCompletionSource? GpuGate { get; set; }
        public TaskCompletionSource? CpuGate { get; set; }
        public TaskCompletionSource? TranscriptionGate { get; set; }
        public TaskCompletionSource TranscriptionEntered { get; } = new();
        public TaskCompletionSource CpuStartEntered { get; } = new();
        public CancellationToken StartToken { get; private set; }
        public string Raw { get; set; } = "привет";
        public (string, string) Parameters { get; private set; }
        private WhisperMode _mode;
        public async Task StartAsync(WhisperMode mode, CancellationToken ct)
        {
            Starts.Add(mode);
            _mode = mode;
            StartToken = ct;
            if (mode == WhisperMode.Cpu) CpuStartEntered.TrySetResult();
            if (mode == WhisperMode.Gpu ? FailGpuStart : FailCpuStart)
                throw new WhisperStartException("секретный сбой");
            var gate = mode == WhisperMode.Gpu ? GpuGate : CpuGate;
            if (gate is not null) await gate.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            IsRunning = true;
        }
        public async Task<string> TranscribeAsync(AudioClip clip, string language, string prompt, CancellationToken ct)
        {
            Clips.Add(clip);
            Parameters = (language, prompt);
            TranscriptionEntered.TrySetResult();
            if (TranscriptionGate is not null) await TranscriptionGate.Task;
            if (_mode == WhisperMode.Gpu ? FailGpuTranscription : FailCpuTranscription)
                throw new InvalidOperationException("секретный сбой");
            return Raw;
        }
        public void Stop() { Stops++; IsRunning = false; }
    }

    private sealed class Inserter : ITextInserter
    {
        public List<string> Texts { get; } = [];
        public bool Fail { get; set; }
        public TaskCompletionSource Inserted { get; } = new();
        public Task InsertAsync(string text)
        {
            if (Fail) throw new InvalidOperationException("секретный текст");
            Texts.Add(text);
            Inserted.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class Ui : IDictationUi
    {
        public List<IndicatorState> Indicators { get; } = [];
        public List<string> Notices { get; } = [];
        public void SetIndicator(IndicatorState state) => Indicators.Add(state);
        public List<NotifyAction> Actions { get; } = [];
        public void Notify(string message, NotifyAction action = NotifyAction.None)
        {
            Notices.Add(message);
            Actions.Add(action);
        }
    }
}
