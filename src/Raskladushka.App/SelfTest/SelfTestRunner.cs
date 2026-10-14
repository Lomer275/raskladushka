using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Raskladushka.App.Dictation;
using Raskladushka.Core.Dictation;
using NAudio.Wave;
using Raskladushka.App.Engine;
using Raskladushka.App.Input;
using Raskladushka.App.Native;
using Raskladushka.App.Shell;
using Raskladushka.Core.Keys;
using Raskladushka.Core.Config;

namespace Raskladushka.App.SelfTest;

public sealed class SelfTestRunner
{
    private readonly AppHost _host;
    private readonly SelfTestForm _form;
    private readonly nint _window;
    private readonly nint _originalLayout;
    private readonly uint _uiThread;
    private static readonly HashSet<string> _reportWords = ["", "ghbdtn ", "ghbdtn", "привет ", "привет",
        "руддщ ", "hello ", "k.,k. ", "люблю ", "ghbdtn vbh ", "привет мир ", "ghbdtn vbh", "привет мир"];

    public SelfTestRunner(AppHost host, SelfTestForm form)
    {
        _host = host;
        _form = form;
        _window = form.Handle;
        _uiThread = NativeMethods.GetWindowThreadProcessId(_window, out _);
        _originalLayout = NativeMethods.GetKeyboardLayout(_uiThread);
    }

    public SelfTestReport Run()
    {
        _host.SelfTestRunning = true;
        try { return RunCases(); }
        finally
        {
            _host.MemoryTrimmer.Touch();
            _host.SelfTestRunning = false;
        }
    }

    private SelfTestReport RunCases()
    {
        var report = new SelfTestReport();
        var paused = _host.Gate.Paused;
        try
        {
            CaptureDiagnostics(report);
            _host.Exceptions.Remove("ghbdtn");
            Case("L1", "привет ; Ru", () => TypeAndCheck(Layout.En, "ghbdtn ", "привет ", Layout.Ru));
            Case("L2", "hello ; En", () => TypeAndCheck(Layout.Ru, "hello ", "hello ", Layout.En));
            Case("L3", "hello ; En / привет ; Ru", () =>
            {
                var en = TypeAndCheck(Layout.En, "hello ", "hello ", Layout.En);
                var ru = TypeAndCheck(Layout.Ru, "ghbdtn ", "привет ", Layout.Ru);
                return ($"{en.Actual} / {ru.Actual}", en.Passed && ru.Passed);
            });
            Case("L4", "люблю ; Ru", () => TypeAndCheck(Layout.En, "k.,k. ", "люблю ", Layout.Ru));
            Case("L5", "привет; Ru", () =>
            {
                Prepare(Layout.En);
                Send("ghbdtn");
                StableText();
                SendFixHotkey();
                return Check("привет", Layout.Ru);
            });
            Case("L6", "ghbdtn ; En; исключение=True", () =>
            {
                Prepare(Layout.En);
                Send("ghbdtn ");
                var before = Check("привет ", Layout.Ru);
                if (!before.Passed) return (before.Actual, false);
                SendFixHotkey();
                var after = Check("ghbdtn ", Layout.En);
                var exception = _host.Exceptions.Contains("ghbdtn");
                return ($"{after.Actual}; исключение={exception}", after.Passed && exception);
            });
            Case("L7", "ghbdtn ; En; исключение=True", () =>
            {
                var result = TypeAndCheck(Layout.En, "ghbdtn ", "ghbdtn ", Layout.En);
                var exception = _host.Exceptions.Contains("ghbdtn");
                return ($"{result.Actual}; исключение={exception}", result.Passed && exception);
            });
            _host.Exceptions.Remove("ghbdtn");
            Case("L8", "привет мир ; Ru", () =>
            {
                Prepare(Layout.En);
                const string keys = "ghbdtn vbh ";
                for (var i = 0; i < keys.Length; i++)
                {
                    if (i > 0) Thread.Sleep(10);
                    Send(keys[i].ToString());
                }
                return Check("привет мир ", Layout.Ru);
            });
            Case("L9", "ghbdtn ; En", () => TypeAndCheck(Layout.En, "ghbdtn ", "ghbdtn ", Layout.En, true));
            Case("L10", "ghbdtn ; En", () =>
            {
                _host.Gate.Paused = true;
                try { return TypeAndCheck(Layout.En, "ghbdtn ", "ghbdtn ", Layout.En); }
                finally { _host.Gate.Paused = paused; }
            });
            Case("C1", "привет мир; Ru", () => ConvertSelectionAndCheck());
            Case("C2", "привет мир; Ru; буфер сохранён=True", () =>
            {
                _host.StaWorker.RunAsync(() => _host.Clipboard.SetText("X-MARK")).GetAwaiter().GetResult();
                var result = ConvertSelectionAndCheck();
                var preserved = false;
                _host.StaWorker.RunAsync(() => preserved = _host.Clipboard.GetText() == "X-MARK").GetAwaiter().GetResult();
                return ($"{result.Actual}; буфер сохранён={preserved}", result.Passed && preserved);
            });
            Case("C3", "привет мир; Ru; картинка сохранена=True", () =>
            {
                _host.StaWorker.RunAsync(() =>
                {
                    using var bitmap = new Bitmap(16, 16);
                    bitmap.SetPixel(0, 0, Color.Red);
                    System.Windows.Forms.Clipboard.SetImage(bitmap);
                }).GetAwaiter().GetResult();
                var result = ConvertSelectionAndCheck();
                var preserved = false;
                _host.StaWorker.RunAsync(() =>
                {
                    using var image = System.Windows.Forms.Clipboard.GetImage();
                    if (image is not null)
                    {
                        using var bitmap = new Bitmap(image);
                        preserved = bitmap.Width == 16 && bitmap.Height == 16 &&
                            bitmap.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb();
                    }
                }).GetAwaiter().GetResult();
                return ($"{result.Actual}; картинка сохранена={preserved}", result.Passed && preserved);
            });
            Case("C4", "; En; буфер сохранён=True", () =>
            {
                _host.StaWorker.RunAsync(() => _host.Clipboard.SetText("X-MARK")).GetAwaiter().GetResult();
                var result = ConvertSelectionAndCheck(empty: true);
                var preserved = false;
                _host.StaWorker.RunAsync(() => preserved = _host.Clipboard.GetText() == "X-MARK").GetAwaiter().GetResult();
                return ($"{result.Actual}; буфер сохранён={preserved}", result.Passed && preserved);
            });
            if (HotkeyNames.IsAltTap(_host.Settings.Layout.FixHotkey))
                Case("C5", "привет мир; Ru; буфер сохранён=True", () =>
                {
                    Prepare(Layout.En);
                    OnUi(() =>
                    {
                        var field = (TextBox)Control.FromHandle(_form.InputHandle)!;
                        field.Text = "ghbdtn vbh";
                        field.Select(field.TextLength, 0);
                    });
                    _host.StaWorker.RunAsync(() => _host.Clipboard.SetText("X-MARK")).GetAwaiter().GetResult();
                    EnsureForeground();
                    InputInjector.Send([
                        InputInjector.Key(0xA0, 0, 0, 0),
                        InputInjector.Key(0x24, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, 0),
                        InputInjector.Key(0x24, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, 0),
                        InputInjector.Key(0xA0, 0, NativeMethods.KEYEVENTF_KEYUP, 0),
                        InputInjector.Key(0xA4, 0, 0, 0),
                        InputInjector.Key(0xA4, 0, NativeMethods.KEYEVENTF_KEYUP, 0)]);
                    Thread.Sleep(600);
                    _host.StaWorker.RunAsync(() => { }).GetAwaiter().GetResult();
                    var result = Check("привет мир", Layout.Ru);
                    var preserved = false;
                    _host.StaWorker.RunAsync(() => preserved = _host.Clipboard.GetText() == "X-MARK").GetAwaiter().GetResult();
                    return ($"{result.Actual}; буфер сохранён={preserved}", result.Passed && preserved);
                });
            RunDictationCases(report);
        }
        finally
        {
            try { _host.StaWorker.RunAsync(System.Windows.Forms.Clipboard.Clear).GetAwaiter().GetResult(); }
            catch (Exception error) { report.CleanupErrors.Add(SafeError(error)); }
            _host.Gate.Paused = paused;
            _host.Router.ResetExternal();
            try { _host.Exceptions.Remove("ghbdtn"); _host.Exceptions.Flush(); }
            catch (Exception error) { report.CleanupErrors.Add(SafeError(error)); }
            try
            {
                OnUi(() => NativeMethods.PostMessageW(_form.InputHandle,
                    NativeMethods.WM_INPUTLANGCHANGEREQUEST, 0, _originalLayout));
                var clock = Stopwatch.StartNew();
                while (NativeMethods.GetKeyboardLayout(_uiThread) != _originalLayout && clock.ElapsedMilliseconds < 2000)
                    Thread.Sleep(20);
                if (NativeMethods.GetKeyboardLayout(_uiThread) != _originalLayout)
                    report.CleanupErrors.Add("Не удалось восстановить исходную раскладку.");
            }
            catch (Exception error) { report.CleanupErrors.Add(SafeError(error)); }
        }
        return report;

        void Case(string name, string expected, Func<(string Actual, bool Passed)> run)
        {
            try
            {
                if (!_host.Ready.GetAwaiter().GetResult()) throw new CaseException("Языковые модели не загружены.");
                if (!_host.HooksInstalled) throw new CaseException("Перехваты не установлены.");
                var result = run();
                report.Cases.Add(new(name, expected, result.Actual, result.Passed));
            }
            catch (Exception error) { report.Cases.Add(new(name, expected, "Не выполнен", false, SafeError(error))); }
        }
    }

    private void RunDictationCases(SelfTestReport report)
    {
        _host.Gate.Paused = true;
        _host.Dictation.CancelAsync().GetAwaiter().GetResult();
        _host.Whisper.Stop();
        Case("D1", "Обе тестовые фразы найдены; журнал без тестовой фразы", () =>
        {
            ValidateWhisperFiles();
            Prepare(Layout.Ru);
            report.Diagnostics["D1.GpuMemoryBeforeMiB"] = ReadGpuMemory();
            using var whisper = new WhisperServerHost(() => _host.Settings.Whisper, () => _host.Settings.Dictation.Language);
            var inserter = new CheckedInserter(_host.TextInserter, EnsureForeground);
            using var worker = CreateWorker(whisper, false, inserter);
            var clock = Stopwatch.StartNew();
            worker.HotkeyDownAsync().GetAwaiter().GetResult();
            worker.HotkeyUpAsync().GetAwaiter().GetResult();
            if (inserter.FocusError is { } focusError) throw focusError;
            var text = StableText();
            report.Diagnostics["D1.PressToTextSeconds"] = clock.Elapsed.TotalSeconds;
            report.Diagnostics["D1.GpuMemoryAfterMiB"] = ReadGpuMemory();
            var first = text.Contains("голосовой ввод", StringComparison.OrdinalIgnoreCase);
            var second = text.Contains("перезапустить контейнер", StringComparison.OrdinalIgnoreCase);
            _host.Log.Flush();
            var path = _host.Log.FilePath;
            var logClean = false;
            var logResult = "ошибка: журнал отсутствует";
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                logClean = !reader.ReadToEnd().Contains("голосовой ввод", StringComparison.OrdinalIgnoreCase);
                logResult = logClean ? "не найдено" : "найдено";
            }
            report.Diagnostics["D1.LogPhrase"] = logResult;
            return ($"Фраза 1: {Found(first)}; фраза 2: {Found(second)}; длина {text.Length}; журнал: {logResult}",
                first && second && logClean);
        });
        Case("D2", "Whisper завершён по простою 3 с в течение 10 с", () =>
        {
            ValidateWhisperFiles();
            using var whisper = new WhisperServerHost(() => _host.Settings.Whisper, () => _host.Settings.Dictation.Language);
            var inserter = new NoOpInserter();
            using var worker = CreateWorker(whisper, true, inserter);
            worker.HotkeyDownAsync().GetAwaiter().GetResult();
            worker.HotkeyUpAsync().GetAwaiter().GetResult();
            if (!whisper.IsRunning || whisper.ProcessId is not int pid)
                return ($"вставка вызвана={inserter.Called}; процесс завершён=неизвестно; ожидание 0 с; Whisper не запущен", false);
            using var process = Process.GetProcessById(pid);
            var clock = Stopwatch.StartNew();
            while (!process.HasExited && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(50);
            return ($"вставка вызвана={inserter.Called}; процесс завершён={process.HasExited}; ожидание {clock.Elapsed.TotalSeconds:F2} с",
                inserter.Called && process.HasExited);
        });
        Case("D3", "Длительность записи от 0,7 до 1,3 с", () =>
        {
            using var recorder = new WasapiRecorder(() => _host.Settings.Dictation.Microphone);
            recorder.Start();
            Thread.Sleep(1000);
            var clip = recorder.Stop();
            return ($"Длительность {clip.Duration.TotalSeconds:F3} с", clip.Duration.TotalSeconds is >= 0.7 and <= 1.3);
        });
        Case("D4", "Рабочий набор до и после освобождения и выделенная память программы записаны", () =>
        {
            var memory = MemoryTrimmer.TrimNow(_host.Log);
            if (memory is not { } measurement)
                return ("Не удалось освободить рабочий набор; ошибка записана в журнал", false);
            report.Diagnostics["D4.WorkingSetBeforeBytes"] = measurement.Before;
            report.Diagnostics["D4.WorkingSetBytes"] = measurement.After;
            report.Diagnostics["D4.PrivateMemoryBytes"] = measurement.Private;
            return ($"Рабочий набор до {measurement.Before}; после {measurement.After}; выделено {measurement.Private} байт", true);
        });

        void Case(string name, string expected, Func<(string Actual, bool Passed)> run)
        {
            try
            {
                var result = run();
                report.Cases.Add(new(name, expected, result.Actual, result.Passed));
            }
            catch (Exception error) { report.Cases.Add(new(name, expected, "Не выполнен", false, SafeError(error))); }
        }
        static string Found(bool found) => found ? "найдено" : "не найдено";
    }

    private DictationWorker CreateWorker(WhisperServerHost whisper, bool shortIdle, ITextInserter inserter)
    {
        AudioClip clip;
        try { clip = VoiceVerifier.LoadSelftestClip(); }
        catch (InvalidDataException error) { throw new CaseException(error.Message); }
        return new DictationWorker(new FileRecorder(clip), whisper,
            inserter, _host.DictationUi,
            () => _host.Settings.Dictation with { IdleUnloadMinutes = shortIdle ? 1 : 5 }, _host.Log.Info,
            shortIdle ? new ShortIdleTimeProvider() : TimeProvider.System);
    }

    private void ValidateWhisperFiles()
    {
        var settings = _host.Settings.Whisper;
        if (!WhisperLayout.IsInstalled(settings, File.Exists))
            throw new CaseException("Голосовой ввод не установлен.");
    }

    private static object ReadGpuMemory()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("nvidia-smi")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    Arguments = "--query-gpu=memory.used --format=csv,noheader,nounits"
                }
            };
            if (!process.Start()) return "недоступно";
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return "недоступно: срок ожидания истёк";
            }
            Task.WhenAll(output, errors).GetAwaiter().GetResult();
            if (process.ExitCode != 0) return "недоступно";
            var lines = output.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) return "недоступно";
            var values = new List<long>();
            foreach (var line in lines)
            {
                if (!long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return "недоступно";
                values.Add(value);
            }
            return values.ToArray();
        }
        catch (Exception) { return "недоступно"; }
    }

    private sealed class FileRecorder(AudioClip clip) : IAudioRecorder
    {
        private readonly AudioClip _clip = clip;
        public void Start() { }
        public AudioClip Stop() => _clip;
        public void Cancel() { }
    }
    private sealed class CheckedInserter(ITextInserter inner, Action check) : ITextInserter
    {
        private readonly ITextInserter _inner = inner;
        private readonly Action _check = check;
        public CaseException? FocusError { get; private set; }
        public Task InsertAsync(string text)
        {
            try { _check(); }
            catch (CaseException error)
            {
                // DictationMachine consumes insertion errors; retain the safe diagnostic for the report.
                FocusError = error;
                throw;
            }
            return _inner.InsertAsync(text);
        }
    }
    private sealed class NoOpInserter : ITextInserter
    {
        private int _called;
        public bool Called => Volatile.Read(ref _called) != 0;
        public Task InsertAsync(string text)
        {
            Volatile.Write(ref _called, 1);
            return Task.CompletedTask;
        }
    }
    private sealed class ShortIdleTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state,
                dueTime == TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(3) : dueTime, period);
    }

    private void CaptureDiagnostics(SelfTestReport report)
    {
        FocusInfo? focus = null;
        try
        {
            OnUi(() => { _form.Activate(); NativeMethods.SetForegroundWindow(_window); _form.Prepare(false); });
            var clock = Stopwatch.StartNew();
            while (NativeMethods.GetForegroundWindow() != _window && clock.ElapsedMilliseconds < 2000) Thread.Sleep(20);
            EnsureForeground();
            Thread.Sleep(400);
            EnsureForeground();
            var current = _host.Focus;
            using var process = Process.GetCurrentProcess();
            // Do not expose another application's process name if activation or focus tracking lagged.
            if (string.Equals(current.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase))
                focus = current;
        }
        catch (Exception) { }

        Add("ProgramVersion", () => typeof(AppHost).Assembly.GetName().Version?.ToString());
        Add("WindowsVersion", () => Environment.OSVersion.VersionString);
        Add("HooksInstalled", () => _host.HooksInstalled);
        Add("LanguageModelsLoaded", () => _host.Ready.GetAwaiter().GetResult());
        Add("Focus.ProcessName", () => focus?.ProcessName);
        Add("Focus.WindowClass", () => string.IsNullOrEmpty(focus?.WindowClass) ? null : focus.WindowClass);
        Add("Focus.IsPassword", () => focus?.IsPassword);
        Add("Focus.IsFullscreen", () => focus?.IsFullscreen);
        Add("TypingAllowed", () => _host.Gate.TypingAllowed);
        Add("AutoSwitchAllowed", () => _host.Gate.AutoSwitchAllowed);
        Add("Paused", () => _host.Gate.Paused);
        Add("InstalledLayouts", () =>
        {
            var count = NativeMethods.GetKeyboardLayoutList(0, null);
            if (count <= 0) return null;
            var layouts = new nint[count];
            count = NativeMethods.GetKeyboardLayoutList(layouts.Length, layouts);
            return count <= 0 ? null : layouts.Take(count).Select(LanguageId).ToArray();
        });
        Add("CurrentLayout", () =>
        {
            var layout = NativeMethods.GetKeyboardLayout(_uiThread);
            return layout == 0 ? null : LanguageId(layout);
        });
        Add("layout.autoSwitch", () => _host.Settings.Layout.AutoSwitch);
        Add("layout.fixHotkey", () => _host.Settings.Layout.FixHotkey);
        Add("layout.minWordLength", () => _host.Settings.Layout.MinWordLength);
        Add("pauseInFullscreen", () => _host.Settings.PauseInFullscreen);
        Add("exclusions.processes.count", () => _host.Settings.Exclusions.Processes.Count);

        void Add(string name, Func<object?> read)
        {
            try { report.Diagnostics[name] = read() ?? "неизвестно"; }
            catch (Exception) { report.Diagnostics[name] = "неизвестно"; }
        }
        static string LanguageId(nint layout) => $"0x{((long)layout & 0xFFFF):X4}";
    }

    private (string Actual, bool Passed) TypeAndCheck(Layout layout, string keys, string expected, Layout target, bool password = false)
    {
        Prepare(layout, password);
        Send(keys);
        return Check(expected, target);
    }

    private void Prepare(Layout layout, bool password = false)
    {
        OnUi(() => { _form.Activate(); NativeMethods.SetForegroundWindow(_window); _form.Prepare(password); });
        var clock = Stopwatch.StartNew();
        while (NativeMethods.GetForegroundWindow() != _window && clock.ElapsedMilliseconds < 2000) Thread.Sleep(20);
        EnsureForeground();
        _host.Router.ResetExternal();
        _host.Layouts.Request(layout);
        clock.Restart();
        while (_host.Layouts.GetCurrent() != layout && clock.ElapsedMilliseconds < 2000)
        {
            EnsureForeground();
            Thread.Sleep(20);
        }
        if (_host.Layouts.GetCurrent() != layout) throw new CaseException("Нужная раскладка недоступна или не переключилась.");
        // Let the focus event and its bounded accessibility probe settle before typing.
        Thread.Sleep(400);
        EnsureForeground();
        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0)
                throw new CaseException("Удерживается клавиша-модификатор.");
        if ((NativeMethods.GetKeyState(0x14) & 1) != 0) throw new CaseException("Включён Caps Lock.");
    }

    private void Send(string keys)
    {
        var inputs = new List<NativeMethods.INPUT>();
        foreach (var key in keys)
        {
            ushort scan = key switch
            {
                'g' => 0x22, 'h' => 0x23, 'b' => 0x30, 'd' => 0x20, 't' => 0x14, 'n' => 0x31,
                'e' => 0x12, 'l' => 0x26, 'o' => 0x18, 'k' => 0x25, '.' => 0x34, ',' => 0x33,
                'v' => 0x2F, ' ' => 0x39,
                _ => throw new CaseException("Неизвестная тестовая клавиша.")
            };
            inputs.Add(InputInjector.Key(0, scan, NativeMethods.KEYEVENTF_SCANCODE, 0));
            inputs.Add(InputInjector.Key(0, scan, NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_KEYUP, 0));
        }
        EnsureForeground();
        InputInjector.Send(inputs.ToArray());
    }

    private (string Actual, bool Passed) ConvertSelectionAndCheck(bool empty = false)
    {
        Prepare(Layout.En);
        OnUi(() =>
        {
            var field = (TextBox)Control.FromHandle(_form.InputHandle)!;
            field.Text = empty ? "" : "ghbdtn vbh";
            field.SelectAll();
        });
        EnsureForeground();
        var vk = FixHotkey(selection: true);
        InputInjector.Send([
            InputInjector.Key(0xA0, 0, 0, 0),
            InputInjector.Key(vk, 0, 0, 0),
            InputInjector.Key(vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0),
            InputInjector.Key(0xA0, 0, NativeMethods.KEYEVENTF_KEYUP, 0)]);
        // The following STA queue drain waits for conversion, including the no-selection timeout.
        Thread.Sleep(600);
        _host.StaWorker.RunAsync(() => { }).GetAwaiter().GetResult();
        return Check(empty ? "" : "привет мир", empty ? Layout.En : Layout.Ru);
    }

    private void SendFixHotkey()
    {
        EnsureForeground();
        var vk = FixHotkey(selection: false);
        InputInjector.Send([InputInjector.Key(vk, 0, 0, 0), InputInjector.Key(vk, 0, NativeMethods.KEYEVENTF_KEYUP, 0)]);
    }

    private ushort FixHotkey(bool selection)
    {
        var name = _host.Settings.Layout.FixHotkey;
        if (HotkeyNames.IsAltTap(name)) return selection ? (ushort)0x13 : (ushort)0xA4;
        HotkeyNames.TryGetVk(name, out var vk);
        return checked((ushort)vk);
    }

    private string StableText()
    {
        var clock = Stopwatch.StartNew();
        var changedAt = 0L;
        var previous = ReadText();
        while (clock.ElapsedMilliseconds < 1500)
        {
            EnsureForeground();
            Thread.Sleep(25);
            var current = ReadText();
            if (current != previous) { previous = current; changedAt = clock.ElapsedMilliseconds; }
            if (clock.ElapsedMilliseconds - changedAt >= 250) return current;
        }
        throw new CaseException("Текст не стабилизировался за 1,5 с.");
    }

    private (string Actual, bool Passed) Check(string expected, Layout target)
    {
        var actual = StableText();
        var clock = Stopwatch.StartNew();
        while (_host.Layouts.GetCurrent() != target && clock.ElapsedMilliseconds < 1000)
        {
            EnsureForeground();
            Thread.Sleep(20);
        }
        EnsureForeground();
        var layout = _host.Layouts.GetCurrent();
        var safe = _reportWords.Contains(actual) ? actual : $"[неожиданный текст, длина {actual.Length}]";
        return ($"{safe}; {layout?.ToString() ?? "неизвестна"}", actual == expected && layout == target);
    }

    private void EnsureForeground()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == _window) return;
        var processName = "неизвестно";
        var windowClass = "неизвестно";
        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            if (pid != 0)
            {
                using var process = Process.GetProcessById(checked((int)pid));
                var name = process.ProcessName;
                if (!string.IsNullOrEmpty(name)) processName = name;
            }
        }
        catch (Exception) { }
        try
        {
            var name = new StringBuilder(256);
            if (NativeMethods.GetClassNameW(window, name, name.Capacity) > 0) windowClass = name.ToString();
        }
        catch (Exception) { }
        throw new CaseException($"Форма самопроверки не активна (активно: {processName}, {windowClass}); посылка клавиш отменена.");
    }
    private string ReadText() => (string)_form.Invoke(new Func<string>(_form.ReadText));
    private void OnUi(Action action) => _form.Invoke(action);
    private static string SafeError(Exception error) => error is CaseException ||
        error is InvalidOperationException { Message: "Windows не приняла все события клавиатуры." } ? error.Message :
        $"Ошибка {error.GetType().Name}, код 0x{error.HResult:X8}.";
    private sealed class CaseException(string message) : Exception(message);
}
