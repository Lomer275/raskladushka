using System.Diagnostics;
using Raskladushka.Core.Shell;
using Raskladushka.App.Engine;
using Raskladushka.App.Dictation;
using Raskladushka.App.Input;
using Raskladushka.App.Native;
using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;
using Raskladushka.Core.Keys;
using Raskladushka.Core.Lang;
using Raskladushka.Core.Typing;
using Microsoft.Win32;

namespace Raskladushka.App.Shell;

public sealed class AppHost : IDisposable
{
    private readonly HookThread _hooks = new();
    private readonly FocusTracker _focus = new();
    private readonly InputInjector _output = new();
    private readonly WasapiRecorder _recorder;
    private readonly IndicatorForm _indicator;
    public WhisperServerHost Whisper { get; }
    public RemoteWhisperClient RemoteWhisper { get; }
    public ISecretProtector SecretProtector { get; } = new DpapiSecretProtector();
    private readonly AsyncLocal<VoiceServerSettings?> _checkServerSettings = new();

    public async Task<RemoteResult> CheckVoiceServerAsync(VoiceServerSettings settings, AudioClip clip, CancellationToken ct)
    {
        // A check uses unsaved fields without exposing them to concurrent dictation.
        _checkServerSettings.Value = settings;
        try
        {
            return await RemoteWhisper.TranscribeAsync(clip, Settings.Dictation.Language, Settings.Dictation.Prompt,
                VoiceServerRoute.TimeoutFor(clip.Duration), ct).ConfigureAwait(false);
        }
        finally { _checkServerSettings.Value = null; }
    }

    public void SaveVoiceServer(VoiceServerSettings settings)
    {
        if (_settingsWatcher is null) throw new InvalidOperationException("Settings watcher unavailable.");
        _settingsWatcher.Update(current => current with { VoiceServer = settings });
    }
    public DictationWorker Dictation { get; }
    public ClipboardInserter TextInserter { get; }
    public DictationUi DictationUi { get; }
    private Settings _settings;
    private bool _started;
    private System.Threading.Timer? _conflictTimer;
    private int _conflictNotified;
    private int _disposed;
    private SettingsWatcher? _settingsWatcher;
    public UpdateChecker Updates { get; private set; } = null!;
    private int _selfTestRunning;
    private int _voiceInstalled;
    private int _voiceSetupOpen;
    private long _lastVoiceReminder = long.MinValue;
    public bool VoiceInstalled => Volatile.Read(ref _voiceInstalled) != 0;
    public bool VoiceSetupOpen
    {
        get => Volatile.Read(ref _voiceSetupOpen) != 0;
        set => Volatile.Write(ref _voiceSetupOpen, value ? 1 : 0);
    }
    public void RefreshVoiceInstalled() => Volatile.Write(ref _voiceInstalled,
        WhisperLayout.IsInstalled(Settings.Whisper, File.Exists) ? 1 : 0);

    private void OnVoiceMissing() => ThreadPool.QueueUserWorkItem(_ =>
    {
        if (VoiceSetupOpen || VoiceInstalled) return;
        var now = Environment.TickCount64;
        var previous = Volatile.Read(ref _lastVoiceReminder);
        if (previous != long.MinValue && now - previous < 600_000) return;
        if (Interlocked.CompareExchange(ref _lastVoiceReminder, now, previous) != previous) return;
        Notify("Голосовой ввод ещё не скачан. Нажмите, чтобы скачать.", NotifyAction.OpenVoiceSetup);
    });
    private readonly object _gateLogSync = new();
    private GateReason? _lastGateReason;
    private FileLog? _wordsLog;
    private int _wordsGeneration;
    public MemoryTrimmer MemoryTrimmer { get; }
    public bool SelfTestRunning
    {
        get => Volatile.Read(ref _selfTestRunning) != 0;
        set => Volatile.Write(ref _selfTestRunning, value ? 1 : 0);
    }
    public FileLog Log { get; }
    public string SettingsPath { get; }
    public ActivityGate Gate { get; }
    public StaWorker StaWorker { get; } = new();
    public ClipboardHelper Clipboard { get; } = new();
    public LayoutApi Layouts { get; } = new();
    public ExceptionsStore Exceptions { get; }
    public InputRouter Router { get; }
    public Task<bool> Ready { get; private set; } = Task.FromResult(false);
    public bool HooksInstalled => _hooks.Installed;
    public FocusInfo Focus => _focus.Current;
    public string? SettingsError { get; }
    public event Action<string, NotifyAction>? Notification;
    public Settings Settings
    {
        get => Volatile.Read(ref _settings);
        set
        {
            var previous = Interlocked.Exchange(ref _settings, value);
            if (previous.Diagnostics.LogWords != value.Diagnostics.LogWords)
                Interlocked.Increment(ref _wordsGeneration);
            if (previous.Diagnostics.LogWords && !value.Diagnostics.LogWords)
            {
                Log.Enqueue(() => { _wordsLog?.Dispose(); _wordsLog = null; });
                Log.Flush();
            }
            Router.ResetExternal();
            LogSettings();
            LogGate();
        }
    }

    public AppHost(FileLog log)
    {
        Log = log;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raskladushka");
        SettingsPath = Path.Combine(directory, "settings.json");
        var loaded = SettingsStore.Load(SettingsPath);
        _settings = loaded.Settings;
        SettingsError = loaded.Error;
        Exceptions = new ExceptionsStore(Path.Combine(directory, "exceptions.txt"));
        Gate = new ActivityGate(_focus, () => Settings);
        var selection = new SelectionConverter(StaWorker, Clipboard, _output, Layouts,
            message => Log.Info($"Selection: {message} [app={ProcessName()}]"));
        _recorder = new WasapiRecorder(() => Settings.Dictation.Microphone);
        Whisper = new WhisperServerHost(() => Settings.Whisper, () => Settings.Dictation.Language);
        RemoteWhisper = new RemoteWhisperClient(new SocketsHttpHandler
        {
            UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        }, () => _checkServerSettings.Value ?? Settings.VoiceServer, SecretProtector, TimeProvider.System);
        _indicator = new IndicatorForm();
        DictationUi = new DictationUi(_indicator, Notify, () => MemoryTrimmer!.Touch());
        MemoryTrimmer = new MemoryTrimmer(() => SelfTestRunning || DictationUi.IsBusy, Log);
        TextInserter = new ClipboardInserter(StaWorker, Clipboard, _output);
        Dictation = new DictationWorker(_recorder, Whisper, TextInserter, DictationUi,
            () => Settings.Dictation, message => Log.Info($"Dictation: {message} [app={ProcessName()}]"), remote: RemoteWhisper);
        static int Metric(int index)
        {
            var value = NativeMethods.GetSystemMetrics(index);
            return value == 0 ? 4 : value;
        }
        var doubleClickMs = NativeMethods.GetDoubleClickTime();
        var hint = new SelectionHint(new(Metric(NativeMethods.SM_CXDRAG), Metric(NativeMethods.SM_CYDRAG),
            Metric(NativeMethods.SM_CXDOUBLECLK), Metric(NativeMethods.SM_CYDOUBLECLK),
            doubleClickMs == 0 ? 500 : (int)doubleClickMs));
        Router = new InputRouter(Gate, selection, () =>
        {
            if (HotkeyNames.IsAltTap(Settings.Layout.FixHotkey)) return 0x13;
            HotkeyNames.TryGetVk(Settings.Layout.FixHotkey, out var vk);
            return vk;
        }, Dictation, () =>
        {
            HotkeyNames.TryGetVk(Settings.Dictation.Hotkey, out var vk);
            return vk;
        }, () => HotkeyNames.IsAltTap(Settings.Layout.FixHotkey), hint,
            () => VoiceSetupOpen ? VoiceKeyState.Busy : (VoiceInstalled || RemoteWhisper.IsConfigured) ? VoiceKeyState.Ready : VoiceKeyState.NotInstalled);
        Router.VoiceMissing += OnVoiceMissing;
        Router.TapBlocked += OnTapBlocked;
        _hooks.Diagnostic = message => Log.Info($"{message}; app={ProcessName()}.");
        _hooks.KeyHandler = Router.OnKey;
        _hooks.Mouse = Router.OnMouse;
        _hooks.MouseButtonDown = Router.OnMouseButtonDown;
        _hooks.InstallationFailed += OnInstallationFailed;
        _focus.Changed += Router.OnFocusChanged;
        Gate.Changed += Router.OnGateChanged;
        _focus.Changed += LogGate;
        Gate.Changed += LogGate;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_started) return;
        _started = true;
        RefreshVoiceInstalled();
        LogSettings();
        _focus.Start();
        LogGate();
        try
        {
            _settingsWatcher = new SettingsWatcher(SettingsPath, () => Settings,
                value => { Settings = value; RefreshVoiceInstalled(); MemoryTrimmer.Touch(); }, Notify, Log);
        }
        catch (Exception error)
        {
            Log.Error("Settings watcher start failed", error);
            Notify("Не удалось включить наблюдение за настройками.");
        }
        Updates = new UpdateChecker(() => Settings, Notify, Log, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raskladushka", "update-check.json"));
        Updates.Start();
        _hooks.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        Ready = Task.Run(() =>
        {
            try
            {
                var detector = new LayoutDetector(LangData.Load(Layout.En), LangData.Load(Layout.Ru), Exceptions.Contains,
                    () => new DetectorOptions { MinWordLength = Settings.Layout.MinWordLength });
                var typing = new TypingController(detector, Exceptions, _output, Layouts, () =>
                {
                    var settings = Settings;
                    HotkeyNames.TryGetVk(settings.Layout.FixHotkey, out var vk);
                    return new TypingOptions(settings.Layout.AutoSwitch && Gate.AutoSwitchAllowed, vk,
                        HotkeyNames.IsAltTap(settings.Layout.FixHotkey));
                }, TimeProvider.System);
                if (Volatile.Read(ref _disposed) != 0) return false;
                typing.Event += OnTypingEvent;
                typing.TapSkipped += OnTapSkipped;
                Router.SetController(typing);
                MemoryTrimmer.Touch();
                return true;
            }
            catch (Exception error)
            {
                Log.Error("Language models load failed", error);
                Notify("Не удалось загрузить языковые модели. Обработка набора отключена.");
                return false;
            }
        });
        if (SettingsError is not null) { Log.Warn("Settings load failed."); Notify(SettingsError); }
        try
        {
            _conflictTimer = new System.Threading.Timer(_ => CheckConflictingApps(), null,
                TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
            ThreadPool.QueueUserWorkItem(_ => CheckStartup());
        }
        catch (Exception error) { Log.Error("Startup checks scheduling failed", error); }
    }

    private void CheckStartup()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var missing = LayoutCheck.Missing(LayoutApi.InstalledLanguageIds());
            if (missing != MissingLayouts.None)
                Log.Info($"Layouts missing: {LayoutCheck.LogValue(missing)}.");
            if (CheckConflictingApps() || Volatile.Read(ref _conflictNotified) != 0) return;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raskladushka");
            var stamp = Path.Combine(directory, "first-run.done");
            if (File.Exists(stamp) || Volatile.Read(ref _disposed) != 0) return;
            Notify(StartupHints.FirstRun(VoiceInstalled, missing));
            try
            {
                Directory.CreateDirectory(directory);
                using var file = File.Create(stamp);
            }
            catch (Exception error) { Log.Warn($"First-run stamp write failed: {error.GetType().Name}."); }
        }
        catch (Exception error) { Log.Error("Startup checks failed", error); }
    }

    private bool CheckConflictingApps()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            var names = new List<string>();
            var processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    try { names.Add(process.ProcessName); }
                    catch (Exception) { }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            var apps = ConflictingApps.Find(names);
            if (apps.Count == 0 || Volatile.Read(ref _disposed) != 0 ||
                Interlocked.CompareExchange(ref _conflictNotified, 1, 0) != 0) return false;
            Log.Info($"Conflicting apps: {string.Join(", ", apps)}.");
            Notify(StartupHints.Conflict(apps));
            return true;
        }
        catch (Exception error) { Log.Error("Conflicting apps check failed", error); return false; }
    }

    public void ToggleAutoSwitch()
    {
        if (_settingsWatcher is null) throw new InvalidOperationException("Settings watcher unavailable.");
        _settingsWatcher.Update(current => current with
        { Layout = current.Layout with { AutoSwitch = !current.Layout.AutoSwitch } });
    }

    public void ToggleLogWords()
    {
        if (_settingsWatcher is null) throw new InvalidOperationException("Settings watcher unavailable.");
        _settingsWatcher.Update(current => current with
        { Diagnostics = current.Diagnostics with { LogWords = !current.Diagnostics.LogWords } });
    }

    public void ToggleUpdateNotify()
    {
        if (_settingsWatcher is null) throw new InvalidOperationException("Settings watcher unavailable.");
        _settingsWatcher.Update(current => current with
        { Updates = current.Updates with { Notify = !current.Updates.Notify } });
    }

    private string ProcessName()
    {
        var name = Focus.ProcessName;
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private void OnTypingEvent(TypingEvent value)
    {
        try
        {
            var app = ProcessName();
            var direction = $"{value.From.ToString().ToLowerInvariant()}->{value.To.ToString().ToLowerInvariant()}";
            Log.Info($"Typing: {value.Kind}; app={app}; {direction}; length={value.Length}; reason={value.Reason}.");
            var generation = Volatile.Read(ref _wordsGeneration);
            if (!Settings.Diagnostics.LogWords || value.Kind == TypingStat.AutoSwitched) return;
            var kind = value.Kind == TypingStat.FixApplied ? "Fix" : "Undo";
            var line = $"{kind}; app={app}; {direction}; reason={value.Reason}; \"{Escape(value.Before)}\" -> \"{Escape(value.After)}\"";
            // Creating the optional logger must stay off the keyboard hook thread.
            Log.Enqueue(() =>
            {
                if (!Settings.Diagnostics.LogWords || generation != Volatile.Read(ref _wordsGeneration)) return;
                (_wordsLog ??= new FileLog("words.log")).Info(line);
            });
        }
        catch (Exception) { }

        static string Escape(string word) => word.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private void OnTapSkipped(TapDiagnostic value)
    {
        try
        {
            var external = value.LastReset == BufferReset.External ? Router.LastExternalReset : null;
            Log.Info($"Typing: TapSkipped; app={ProcessName()}; reason={value.Reason}; disarm={value.Disarm}; selection={value.SelectionLikely}; " +
                $"lastReset={value.LastReset}; resetAgeMs={value.LastResetAgeMs?.ToString() ?? "-"}; " +
                $"external={external?.Cause.ToString() ?? "-"}; externalAgeMs={external?.AgeMs.ToString() ?? "-"}.");
        }
        catch (Exception) { }
    }

    private void OnTapBlocked(GateReason? reason)
    {
        try
        {
            Log.Info(reason is { } gate
                ? $"Typing: TapSkipped; app={ProcessName()}; reason=Gate; gate={gate}."
                : $"Typing: TapSkipped; app={ProcessName()}; reason=NotReady.");
        }
        catch (Exception) { }
    }

    private void LogSettings()
    {
        var settings = Settings;
        var whisper = Core.Dictation.WhisperLayout.HasBuild(settings.Whisper, Core.Dictation.WhisperMode.Gpu, File.Exists) &&
            Core.Dictation.WhisperLayout.ModelFor(settings.Whisper, Core.Dictation.WhisperMode.Gpu, File.Exists) is not null
            ? "gpu"
            : Core.Dictation.WhisperLayout.HasBuild(settings.Whisper, Core.Dictation.WhisperMode.Cpu, File.Exists) &&
                Core.Dictation.WhisperLayout.ModelFor(settings.Whisper, Core.Dictation.WhisperMode.Cpu, File.Exists) is not null
                ? "cpu" : "missing";
        Log.Info($"Settings: autoSwitch={settings.Layout.AutoSwitch}; fixHotkey={settings.Layout.FixHotkey}; " +
            $"dictationHotkey={settings.Dictation.Hotkey}; pauseInFullscreen={settings.PauseInFullscreen}; " +
            $"exclusions={settings.Exclusions.Processes.Count}; logWords={settings.Diagnostics.LogWords}; whisper={whisper}.");
    }

    private void LogGate()
    {
        lock (_gateLogSync)
        {
            var reason = Gate.Reason;
            if (reason == GateReason.Checking) return;
            if (_lastGateReason == reason) return;
            _lastGateReason = reason;
            Log.Info(reason == GateReason.None ? $"Gate: typing=on; app={ProcessName()}." :
                $"Gate: typing=off; reason={reason.ToString().ToLowerInvariant()}; app={ProcessName()}.");
        }
    }

    private void Notify(string message) => Notify(message, NotifyAction.None);

    private void Notify(string message, NotifyAction action = NotifyAction.None)
    {
        if (message == "Whisper не запустился. Подробности в журнале." &&
            RemoteWhisper.IsConfigured && !VoiceInstalled)
        {
            message = "Голосовой ввод ещё не скачан. Нажмите, чтобы скачать.";
            action = NotifyAction.OpenVoiceSetup;
        }
        if (Volatile.Read(ref _disposed) == 0)
            try { Notification?.Invoke(message, action); }
            catch (Exception error) { Log.Error("Notification failed", error); }
    }
    private void OnInstallationFailed()
    {
        Log.Error("Hook installation failed.");
        Notify("Не удалось установить перехват клавиатуры и мыши. Обработка набора отключена.");
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) { Router.ResetExternal(); _hooks.Reinstall(); }
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) { Router.ResetExternal(); _hooks.Reinstall(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        Release(() => _conflictTimer?.Dispose());
        Release(() => _settingsWatcher?.Dispose());
        Release(() => Updates?.Dispose());
        Release(MemoryTrimmer.Dispose);
        Release(_hooks.Dispose);
        Release(Dictation.Dispose);
        Release(RemoteWhisper.Dispose);
        Release(Whisper.Dispose);
        Release(_recorder.Dispose);
        Release(_indicator.Dispose);
        Release(StaWorker.Dispose);
        Release(_focus.Dispose);
        Release(Exceptions.Dispose);
        Release(Log.Flush);
        Release(() => _wordsLog?.Dispose());

        void Release(Action dispose)
        {
            try { dispose(); }
            catch (Exception error) { Log.Error("Shutdown cleanup failed", error); }
        }
    }
}
