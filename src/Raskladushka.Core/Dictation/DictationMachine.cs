using Raskladushka.Core.Config;

namespace Raskladushka.Core.Dictation;

public sealed class DictationMachine
{
    public const string CpuNotice = "Видеокарта не отвечает — распознаю на процессоре, это медленнее.";
    public const string SlowCpuNotice = "Видеокарта не отвечает — распознаю на процессоре, это очень медленно. Быстрее станет, если скачать модель для процессора: меню значка → «Голосовой ввод…».";
    public const string MicrophoneNotFoundNotice = "Микрофон не найден. Подключите микрофон и попробуйте снова.";
    public const string MicrophoneAccessDeniedNotice = "Windows не даёт программам доступ к микрофону. Включите: Параметры → Конфиденциальность → Микрофон → «Разрешить классическим приложениям доступ к микрофону».";
    public const string MicrophoneUnavailableNotice = "Микрофон недоступен.";
    public const string DigitalSilenceNotice = "Микрофон передаёт тишину. Проверьте, что он включён и что Windows разрешает доступ: Параметры → Конфиденциальность → Микрофон.";
    public const double SilenceRms = 0.005;
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MaxRecording = TimeSpan.FromSeconds(120);

    private readonly object _gate = new();
    private readonly IAudioRecorder _recorder;
    private readonly IWhisperHost _whisper;
    private readonly ITextInserter _inserter;
    private readonly IDictationUi _ui;
    private readonly TimeProvider _time;
    private readonly Func<DictationSettings> _settings;
    private readonly Action<string> _log;
    private readonly IRemoteTranscriber? _remote;
    private readonly VoiceServerRoute _route;
    private bool _serverConfigured;
    private bool _useServer;
    private CancellationTokenSource? _requestCancellation;
    private DictationState _state;
    private WhisperMode _mode;
    private Task? _startup;
    private CancellationTokenSource? _hostCancellation;
    private ITimer? _recordingTimer;
    private ITimer? _idleTimer;
    private long _recordingGeneration;
    private long _idleGeneration;
    private bool _suspended;
    private long _suspensionGeneration;

    public DictationMachine(IAudioRecorder recorder, IWhisperHost whisper, ITextInserter inserter,
        IDictationUi ui, TimeProvider time, Func<DictationSettings> settings, Action<string> log, IRemoteTranscriber? remote = null)
    {
        _recorder = recorder;
        _whisper = whisper;
        _inserter = inserter;
        _ui = ui;
        _time = time;
        _settings = settings;
        _log = log;
        _remote = remote;
        _route = new(time);
    }

    public DictationState State { get { lock (_gate) return _state; } }

    public void OnHotkeyDown()
    {
        lock (_gate)
        {
            if (_suspended || _state != DictationState.Idle) return;
            try
            {
                _idleGeneration++;
                _idleTimer?.Dispose();
                _idleTimer = null;
                _serverConfigured = _remote?.IsConfigured == true;
                _useServer = _serverConfigured && !_route.InCooldown;
                _recorder.Start();
                _state = DictationState.Recording;
                SetIndicator(IndicatorState.Recording);
                var generation = ++_recordingGeneration;
                _recordingTimer = _time.CreateTimer(_ => { _ = ReleaseAsync(generation); }, null,
                    MaxRecording, Timeout.InfiniteTimeSpan);
                if (!_useServer && !_whisper.IsRunning && (_startup is null || _startup.IsCompleted))
                {
                    ResetHostState();
                    _hostCancellation = new();
                    _startup = StartHostAsync(_hostCancellation.Token);
                    _ = ObserveStartupAsync(_startup);
                }
                Log("Recording started.");
            }
            catch (Exception error)
            {
                var problem = (error as MicrophoneException)?.Problem;
                Log($"Microphone start failed: {problem?.ToString() ?? "other"}.");
                Notify(problem switch
                {
                    MicrophoneProblem.NotFound => MicrophoneNotFoundNotice,
                    MicrophoneProblem.AccessDenied => MicrophoneAccessDeniedNotice,
                    _ => MicrophoneUnavailableNotice
                }, problem == MicrophoneProblem.AccessDenied ? NotifyAction.OpenMicrophoneSettings : NotifyAction.None);
                TryCancelRecording();
                ReturnToIdle();
            }
        }
    }

    public void Suspend()
    {
        lock (_gate)
        {
            if (_suspended) return;
            _suspended = true;
            _suspensionGeneration++;
            _idleGeneration++;
            _idleTimer?.Dispose();
            _idleTimer = null;
            if (_state == DictationState.Recording)
            {
                TryCancelRecording();
                ReturnToIdle();
            }
            try { _requestCancellation?.Cancel(); }
            catch { Log("Server cancellation failed."); }
            StopHost();
            Log("Dictation suspended.");
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_suspended) return;
            _suspended = false;
            Log("Dictation resumed.");
        }
    }

    public void OnOtherKeyWhileHeld()
    {
        lock (_gate)
        {
            if (_state != DictationState.Recording) return;
            TryCancelRecording();
            Log("Recording cancelled.");
            ReturnToIdle();
        }
    }

    public Task OnHotkeyUpAsync() => ReleaseAsync(null);

    private Task ReleaseAsync(long? generation)
    {
        AudioClip clip;
        DictationSettings settings;
        Task startup;
        DateTimeOffset deadline;
        long suspensionGeneration;
        bool useServer;
        bool serverConfigured;
        lock (_gate)
        {
            if (_state != DictationState.Recording ||
                (generation.HasValue && generation != _recordingGeneration))
                return Task.CompletedTask;
            _state = DictationState.Processing;
            try
            {
                deadline = _time.GetUtcNow() + StartTimeout;
                _recordingTimer?.Dispose();
                _recordingTimer = null;
                clip = _recorder.Stop();
                settings = _settings();
                Log($"Recording duration: {clip.Duration.TotalSeconds:F3} s.");
                if (clip.Duration.TotalSeconds < settings.MinSeconds)
                {
                    Log("Recording discarded.");
                    ReturnToIdle();
                    return Task.CompletedTask;
                }
                if (clip.Rms == 0)
                {
                    Notify(DigitalSilenceNotice, NotifyAction.OpenMicrophoneSettings);
                    Log("Recording is digital silence.");
                    ReturnToIdle();
                    return Task.CompletedTask;
                }
                if (clip.Rms < SilenceRms)
                {
                    Log("Recording discarded.");
                    ReturnToIdle();
                    return Task.CompletedTask;
                }
                suspensionGeneration = _suspensionGeneration;
                useServer = _useServer;
                serverConfigured = _serverConfigured;
                startup = useServer ? Task.CompletedTask : _startup ?? Task.CompletedTask;
                if (!startup.IsCompleted) SetIndicator(IndicatorState.LoadingModel);
            }
            catch
            {
                Log("Recording stop failed.");
                Notify(MicrophoneUnavailableNotice);
                TryCancelRecording();
                ReturnToIdle();
                return Task.CompletedTask;
            }
        }
        return ProcessAsync(clip, settings, startup, deadline, suspensionGeneration, useServer, serverConfigured);
    }

    private async Task ProcessAsync(AudioClip clip, DictationSettings settings, Task startup,
        DateTimeOffset deadline, long suspensionGeneration, bool useServer, bool serverConfigured)
    {
        var starting = true;
        var inserting = false;
        try
        {
            var began = _time.GetUtcNow();
            string? raw = null;
            if (useServer)
            {
                RemoteResult result;
                using var request = new CancellationTokenSource();
                try
                {
                    Task<RemoteResult> pending;
                    lock (_gate)
                    {
                        if (DropSuspendedResult(suspensionGeneration)) return;
                        _requestCancellation = request;
                        SetIndicator(IndicatorState.Transcribing);
                        pending = _remote!.TranscribeAsync(clip, settings.Language, settings.Prompt,
                            VoiceServerRoute.TimeoutFor(clip.Duration), request.Token);
                    }
                    result = await pending.WaitAsync(request.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    lock (_gate)
                    {
                        if (DropSuspendedResult(suspensionGeneration)) return;
                    }
                    result = new(RemoteOutcome.Failed, null, null, 0, _time.GetUtcNow() - began);
                }
                finally
                {
                    lock (_gate) _requestCancellation = null;
                }
                lock (_gate)
                {
                    if (DropSuspendedResult(suspensionGeneration)) return;
                    LogRoute(clip, result);
                    var notice = _route.OnOutcome(result.Outcome);
                    if (notice is null) raw = result.Text ?? "";
                    else
                    {
                        Notify(notice);
                        deadline = _time.GetUtcNow() + StartTimeout;
                        if (!_whisper.IsRunning && (_startup is null || _startup.IsCompleted))
                        {
                            ResetHostState();
                            _hostCancellation = new();
                            _startup = StartHostAsync(_hostCancellation.Token);
                            _ = ObserveStartupAsync(_startup);
                        }
                        startup = _startup ?? Task.CompletedTask;
                    }
                }
            }
            else if (serverConfigured)
                LogRoute(clip, null);
            if (raw is null)
            {
                await WaitForStartupAsync(startup, deadline).ConfigureAwait(false);
                starting = false;
                try
                {
                    raw = await TranscribeAsync(clip, settings, suspensionGeneration).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Task cpuStartup;
                    lock (_gate)
                    {
                        if (DropSuspendedResult(suspensionGeneration)) return;
                        if (_mode == WhisperMode.Cpu) throw;
                        LogFailure("Transcription failed", error);
                        starting = true;
                        cpuStartup = SwitchToCpu();
                        _startup = cpuStartup;
                        _ = ObserveStartupAsync(cpuStartup);
                    }
                    await WaitForStartupAsync(cpuStartup, deadline).ConfigureAwait(false);
                    starting = false;
                    raw = await TranscribeAsync(clip, settings, suspensionGeneration).ConfigureAwait(false);
                }
            }
            Task? insertion = null;
            string? text;
            lock (_gate)
            {
                if (DropSuspendedResult(suspensionGeneration)) return;
                text = TranscriptCleaner.Clean(raw);
                Log($"Transcription duration: {(_time.GetUtcNow() - began).TotalSeconds:F3} s; text length: {text?.Length ?? 0}.");
                if (text is not null)
                {
                    inserting = true;
                    insertion = _inserter.InsertAsync(text);
                }
            }
            if (insertion is not null)
            {
                await insertion.ConfigureAwait(false);
                lock (_gate) Log($"Text inserted; length: {text!.Length}.");
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (DropSuspendedResult(suspensionGeneration)) return;
                LogFailure(inserting ? "Insertion failed" : starting ? "Whisper start failed" : "Transcription failed", error);
                if (starting)
                {
                    StopHost();
                    Notify("Whisper не запустился. Подробности в журнале.");
                }
                else if (!inserting)
                    Notify("Не удалось распознать речь. Подробности в журнале.");
            }
        }
        finally
        {
            lock (_gate) ReturnToIdle();
        }
    }

    private void LogRoute(AudioClip clip, RemoteResult? result)
    {
        var reason = result?.Outcome switch
        {
            RemoteOutcome.Ok => "ok",
            RemoteOutcome.Unreachable => "unreachable",
            RemoteOutcome.Busy => "busy",
            RemoteOutcome.Timeout => "timeout",
            RemoteOutcome.KeyRejected => "key",
            RemoteOutcome.Failed => "failed",
            _ => "cooldown"
        };
        var destination = result?.Outcome == RemoteOutcome.Ok ? "server" : "local";
        var upload = result is null ? "-" : result.UploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var status = result?.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
        var elapsed = result is null ? "-" : result.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " s";
        Log(FormattableString.Invariant($"Voice route: {destination}; reason={reason}; audio={clip.Duration.TotalSeconds:F1} s; upload={upload}{(result is null ? "" : " B")}; status={status}; elapsed={elapsed}."));
    }

    private bool DropSuspendedResult(long generation)
    {
        if (!_suspended && generation == _suspensionGeneration) return false;
        Log("Dictation result dropped: suspended.");
        return true;
    }

    private async Task WaitForStartupAsync(Task startup, DateTimeOffset deadline)
    {
        if (!startup.IsCompleted)
        {
            lock (_gate) SetIndicator(IndicatorState.LoadingModel);
            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            await startup.WaitAsync(remaining, _time).ConfigureAwait(false);
        }
        else
            await startup.ConfigureAwait(false);
    }

    private Task<string> TranscribeAsync(AudioClip clip, DictationSettings settings, long suspensionGeneration)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_suspended || suspensionGeneration != _suspensionGeneration)
                throw new OperationCanceledException();
            SetIndicator(IndicatorState.Transcribing);
            token = _hostCancellation?.Token ?? CancellationToken.None;
            return _whisper.TranscribeAsync(clip, settings.Language, settings.Prompt, token);
        }
    }

    private async Task StartHostAsync(CancellationToken token)
    {
        if (!_whisper.HasGpuBuild)
        {
            _mode = WhisperMode.Cpu;
            Log("Whisper GPU build not installed; starting on CPU.");
            await StartModeAsync(WhisperMode.Cpu, token).ConfigureAwait(false);
            return;
        }
        try
        {
            await StartModeAsync(WhisperMode.Gpu, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Task cpuStartup;
            lock (_gate)
            {
                LogFailure("Whisper startup attempt failed", error);
                token.ThrowIfCancellationRequested();
                cpuStartup = SwitchToCpu();
            }
            await cpuStartup.ConfigureAwait(false);
        }
    }

    private async Task StartModeAsync(WhisperMode mode, CancellationToken token)
    {
        var began = _time.GetTimestamp();
        await _whisper.StartAsync(mode, token).ConfigureAwait(false);
        lock (_gate) Log($"Whisper ready; mode={mode}; startup: {_time.GetElapsedTime(began).TotalSeconds:F3} s.");
    }

    private void LogFailure(string context, Exception error) =>
        Log($"{context}: {error.GetType().Name}." +
            (error is WhisperStartException && !_serverConfigured ? $" {error.Message}" : ""));

    private Task SwitchToCpu()
    {
        _whisper.Stop();
        _mode = WhisperMode.Cpu;
        Notify(_whisper.HasCpuModel ? CpuNotice : SlowCpuNotice);
        Log("Whisper switching to CPU.");
        return StartModeAsync(WhisperMode.Cpu, _hostCancellation?.Token ?? CancellationToken.None);
    }

    private async Task ObserveStartupAsync(Task startup)
    {
        // Cancellation may discard a recording before anyone awaits its startup.
        try { await startup.ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) LogFailure("Whisper startup attempt failed", error); }
    }

    private void ReturnToIdle()
    {
        _state = DictationState.Idle;
        SetIndicator(IndicatorState.Hidden);
        try
        {
            _recordingGeneration++;
            _recordingTimer?.Dispose();
            _recordingTimer = null;
            _idleTimer?.Dispose();
            var generation = ++_idleGeneration;
            _idleTimer = null;
            if (_suspended) return;
            _idleTimer = _time.CreateTimer(_ => UnloadIdle(generation), null,
                TimeSpan.FromMinutes(_settings().IdleUnloadMinutes), Timeout.InfiniteTimeSpan);
        }
        catch { Log("Timer scheduling failed."); }
    }

    private void UnloadIdle(long generation)
    {
        lock (_gate)
        {
            if (_suspended || _state != DictationState.Idle || generation != _idleGeneration) return;
            _idleGeneration++;
            StopHost();
        }
    }

    private void StopHost()
    {
        ResetHostState();
        try { _whisper.Stop(); }
        catch { Log("Whisper stop failed."); }
        Log("Whisper stopped.");
    }

    private void ResetHostState()
    {
        try { _hostCancellation?.Cancel(); }
        catch { Log("Whisper cancellation failed."); }
        _hostCancellation?.Dispose();
        _hostCancellation = null;
        _startup = null;
        _mode = WhisperMode.Gpu;
    }

    private void TryCancelRecording()
    {
        try { _recorder.Cancel(); }
        catch { Log("Recording cancellation failed."); }
    }

    private void SetIndicator(IndicatorState state)
    {
        try { _ui.SetIndicator(state); }
        catch { Log("Indicator update failed."); }
    }

    private void Notify(string message, NotifyAction action = NotifyAction.None)
    {
        try { _ui.Notify(message, action); }
        catch { Log("Notification failed."); }
    }

    private void Log(string message)
    {
        try { _log(message); }
        catch { /* Logging must not break the keyboard event path. */ }
    }
}
