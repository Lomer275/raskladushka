using System.Diagnostics;
using System.Runtime.InteropServices;
using Raskladushka.App.Native;
using Raskladushka.Core.Typing;

namespace Raskladushka.App.Input;

public sealed class HookThread : IDisposable
{
    private readonly object _lifecycle = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly NativeMethods.HookProc _keyboardProc;
    private readonly NativeMethods.HookProc _mouseProc;
    private readonly bool[] _down = new bool[256];
    private Thread? _thread;
    private uint _threadId;
    private nint _keyboard, _mouse;
    private bool _disposed;
    private static readonly (int Vk, string Name)[] ModifierKeys =
    [
        (0xA0, "LShift"), (0xA1, "RShift"), (0xA2, "LControl"), (0xA3, "RControl"),
        (0xA4, "LAlt"), (0xA5, "RAlt"), (0x5B, "LWin"), (0x5C, "RWin")
    ];
    private long _keyboardSeen, _mouseSeen, _lastKeyboardSeen, _lastMouseSeen;
    private uint _lastInput;
    private bool _needsBaseline = true;
    private long? _lastFailureLog, _lastMissLog;
    private long _suppressedFailures, _missedInput;
    public Action<string>? Diagnostic;
    public Func<KeyEvent, Modifiers, bool>? KeyHandler;
    public Action? MouseButtonDown;
    public Action<MouseInput>? Mouse;
    public event Action? InstallationFailed;
    public bool Installed { get; private set; }

    public HookThread()
    {
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Raskladushka keyboard" };
            _thread.Start();
            _ready.Wait();
        }
    }

    public void Reinstall()
    {
        lock (_lifecycle)
            if (!_disposed && _threadId != 0)
                NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_REINSTALL, 0, 0);
    }

    private void Run()
    {
        nuint timer = 0;
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessageW(out _, 0, 0, 0, 0);
            Install("start");
            timer = NativeMethods.SetTimer(0, 0, 1000, 0);
            _ready.Set();
            while (NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == NativeMethods.WM_REINSTALL) { Uninstall(); Install("reinstall"); }
                else if (message.Message == NativeMethods.WM_TIMER && message.WParam == timer) Watchdog();
                else { NativeMethods.TranslateMessage(in message); NativeMethods.DispatchMessageW(in message); }
            }
        }
        finally
        {
            if (timer != 0) NativeMethods.KillTimer(0, timer);
            Uninstall();
            _ready.Set();
        }
    }

    private void Install(string reason)
    {
        _needsBaseline = true;
        Array.Clear(_down);
        foreach (var vk in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            _down[vk] = (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        var module = NativeMethods.GetModuleHandleW(null);
        _keyboard = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouse = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        Installed = _keyboard != 0 && _mouse != 0;
        if (Installed) Report($"Hook: installed; reason={reason}");
        else
        {
            Uninstall();
            try { InstallationFailed?.Invoke(); } catch (Exception) { }
        }
    }

    private void Uninstall()
    {
        if (_keyboard != 0) NativeMethods.UnhookWindowsHookEx(_keyboard);
        if (_mouse != 0) NativeMethods.UnhookWindowsHookEx(_mouse);
        _keyboard = _mouse = 0;
        Installed = false;
    }

    private nint OnKeyboard(int code, nuint wParam, nint lParam)
    {
        _keyboardSeen++;
        if (code < 0) return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (data.ExtraInfo == InputInjector.Tag) return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
        var started = Stopwatch.GetTimestamp();
        var down = (data.Flags & NativeMethods.LLKHF_UP) == 0;
        try
        {
            var vk = (int)data.VkCode;
            var extended = (data.Flags & NativeMethods.LLKHF_EXTENDED) != 0;
            var modifier = vk switch
            {
                0x10 => data.ScanCode == 0x36 ? 0xA1 : 0xA0,
                0x11 => extended ? 0xA3 : 0xA2,
                0x12 => extended ? 0xA5 : 0xA4,
                _ => vk
            };
            var repeat = down && modifier is >= 0 and < 256 && _down[modifier];
            if (modifier is >= 0 and < 256) _down[modifier] = down;
            if (down)
                foreach (var key in ModifierKeys)
                    if (key.Vk != modifier && _down[key.Vk] && (NativeMethods.GetAsyncKeyState(key.Vk) & 0x8000) == 0)
                    {
                        _down[key.Vk] = false;
                        Report($"Hook: stuck modifier cleared; key={key.Name}");
                    }
            var mods = new Modifiers(_down[0xA0] || _down[0xA1], _down[0xA2] || _down[0xA3],
                _down[0xA4] || _down[0xA5], _down[0x5B] || _down[0x5C], (NativeMethods.GetKeyState(0x14) & 1) != 0);
            if (KeyHandler?.Invoke(new KeyEvent(vk, (int)data.ScanCode, extended, down, repeat), mods) == true) return 1;
        }
        catch (Exception error)
        {
            var now = Environment.TickCount64;
            if (_lastFailureLog is null || now - _lastFailureLog.Value >= 60_000)
            {
                Report($"Hook: handler failed; error={error.GetType().Name}; site={error.TargetSite?.Name}; suppressed={_suppressedFailures}");
                _lastFailureLog = now;
                _suppressedFailures = 0;
            }
            else _suppressedFailures++;
        }
        finally
        {
            var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= 100) Report($"Hook: slow handler; hook=keyboard; event={(down ? "down" : "up")}; ms={ms}");
        }
        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }

    private nint OnMouse(int code, nuint wParam, nint lParam)
    {
        _mouseSeen++;
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Alt+wheel and Alt+side button are chords: they must cancel a pending Alt tap like a click does.
            var altChord = wParam is 0x020A or 0x020B or 0x020E && (_down[0xA4] || _down[0xA5]);
            if (code >= 0 && (altChord || wParam is 0x0201 or 0x0204 or 0x0207))
                MouseButtonDown?.Invoke();
            if (code >= 0 && wParam is 0x0201 or 0x0202 or 0x0204 or 0x0207 or 0x020B)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var button = wParam switch
                {
                    0x0204 => MouseButton.Right,
                    0x0207 => MouseButton.Middle,
                    0x020B => MouseButton.X,
                    _ => MouseButton.Left
                };
                Mouse?.Invoke(new(button, wParam != 0x0202, data.Point.X, data.Point.Y,
                    data.Time, _down[0xA0] || _down[0xA1]));
            }
        }
        catch (Exception) { }
        finally
        {
            var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= 100) Report($"Hook: slow handler; hook=mouse; ms={ms}");
        }
        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }

    private void Report(string message)
    {
        try { Diagnostic?.Invoke(message); } catch (Exception) { }
    }

    private void Watchdog()
    {
        if (!Installed) return;
        var desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        if (desktop == 0)
        {
            _needsBaseline = true;
            return;
        }
        NativeMethods.CloseDesktop(desktop);
        var input = new NativeMethods.LASTINPUTINFO { Size = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref input))
        {
            _needsBaseline = true;
            return;
        }
        if (_needsBaseline) _needsBaseline = false;
        else if (input.Time != _lastInput && _keyboardSeen == _lastKeyboardSeen && _mouseSeen == _lastMouseSeen)
        {
            Uninstall();
            Install("watchdog");
            _missedInput++;
            var now = Environment.TickCount64;
            if (_lastMissLog is null || now - _lastMissLog.Value >= 60_000)
            {
                Report($"Hook: input missed by hooks; count={_missedInput}");
                _lastMissLog = now;
                _missedInput = 0;
            }
        }
        _lastInput = input.Time;
        _lastKeyboardSeen = _keyboardSeen;
        _lastMouseSeen = _mouseSeen;
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
            if (_threadId != 0) NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, 0, 0);
        }
        if (thread != Thread.CurrentThread) thread?.Join();
    }
}
