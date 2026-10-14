using System.Runtime.InteropServices;
using System.Text;
using Raskladushka.App.Native;

namespace Raskladushka.App.Engine;

public sealed record FocusInfo(string ProcessName, string WindowClass, bool IsPassword, bool IsFullscreen, bool Checking);

public sealed class FocusTracker : IDisposable
{
    private readonly object _lifecycle = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly NativeMethods.WinEventProc _callback;
    private FocusInfo _current = new("", "", false, false, false);
    private Thread? _thread;
    private uint _threadId;
    private bool _disposed;
    private long _generation, _deadline;
    private bool _pending;
    private bool _hardRefresh;
    private nint _foreground, _focusWindow;
    private nuint _timer;
    private nuint _fullscreenTimer;
    private Func<bool>? _passwordQuery;
    private long _probeGeneration;
    private Task<(long Generation, bool Password)>? _probe;
    public FocusInfo Current => Volatile.Read(ref _current);
    public event Action? Changed;

    public FocusTracker() => _callback = OnEvent;

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Raskladushka focus" };
            _thread.Start();
            _ready.Wait();
        }
    }

    private void Run()
    {
        nint foregroundHook = 0, focusHook = 0;
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessageW(out _, 0, 0, 0, 0);
            foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND, 0, _callback, 0, 0, 0);
            focusHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_FOCUS,
                NativeMethods.EVENT_OBJECT_FOCUS, 0, _callback, 0, 0, 0);
            _fullscreenTimer = NativeMethods.SetTimer(0, 0, 2000, 0);
            Refresh(0, 0, 0);
            _ready.Set();
            while (NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == NativeMethods.WM_TIMER) Tick(message.WParam == _fullscreenTimer);
                else { NativeMethods.TranslateMessage(in message); NativeMethods.DispatchMessageW(in message); }
            }
        }
        catch (Exception) { Publish(new("", "", false, false, false)); }
        finally
        {
            if (_timer != 0) NativeMethods.KillTimer(0, _timer);
            if (_fullscreenTimer != 0) NativeMethods.KillTimer(0, _fullscreenTimer);
            if (foregroundHook != 0) NativeMethods.UnhookWinEvent(foregroundHook);
            if (focusHook != 0) NativeMethods.UnhookWinEvent(focusHook);
            _ready.Set();
        }
    }

    private void OnEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint threadId, uint time)
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            var activeThread = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
            if (eventType == NativeMethods.EVENT_OBJECT_FOCUS && activeThread != threadId) return;
            Refresh(eventType == NativeMethods.EVENT_OBJECT_FOCUS ? hwnd : 0, objectId, childId,
                eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND);
        }
        catch (Exception) { Publish(new("", "", false, false, false), true); }
    }

    private void Refresh(nint eventWindow, int objectId, int childId, bool foregroundEvent = false)
    {
        var generation = ++_generation;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0)
        {
            _foreground = _focusWindow = 0;
            _pending = false;
            StopProbeTimer();
            Publish(new("", "", false, false, false), true);
            return;
        }
        var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out var process);
        var info = new NativeMethods.GUITHREADINFO { Size = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        var focus = thread != 0 && NativeMethods.GetGUIThreadInfo(thread, ref info) && info.Focus != 0 ? info.Focus : hwnd;
        var hard = foregroundEvent || _foreground == 0 || hwnd != _foreground || focus != _focusWindow;
        _foreground = hwnd;
        _focusWindow = focus;
        var name = new StringBuilder(256);
        var windowClass = NativeMethods.GetClassNameW(focus, name, name.Capacity) > 0 ? name.ToString() : "";
        // Same-window events may replace the query, but must not extend a pending hard refresh.
        if (hard || !_pending || !_hardRefresh)
        {
            _deadline = Environment.TickCount64 + 300;
            _hardRefresh = hard;
        }
        _pending = true;
        if (hard) Publish(new(ReadProcessName(process), windowClass, true, ReadFullscreen(), true), true);
        _passwordQuery = () => ReadPassword(focus, windowClass, eventWindow, objectId, childId);
        StartProbe(generation);
        _timer = NativeMethods.SetTimer(0, _timer, 25, 0);
    }

    private void StartProbe(long generation)
    {
        // A hung accessibility provider must not create an unbounded number of blocked threads.
        if ((_probe is null || _probe.IsCompleted) && _probeGeneration != generation && _passwordQuery is { } query)
        {
            _probeGeneration = generation;
            _probe = Task.Run(() => (generation, query()));
        }
    }

    private void StopProbeTimer()
    {
        if (_timer != 0) NativeMethods.KillTimer(0, _timer);
        _timer = 0;
    }

    private void Tick(bool refreshFullscreen)
    {
        var now = Environment.TickCount64;
        if (_pending)
        {
            if (now < _deadline) StartProbe(_generation);
            if (_probe is { IsCompletedSuccessfully: true } && _probe.Result.Generation == _generation && now <= _deadline)
            {
                _pending = false;
                Publish(Current with { IsPassword = _probe.Result.Password, Checking = false });
            }
            else if (now >= _deadline)
            {
                _pending = false;
                Publish(Current with { IsPassword = !_hardRefresh && Current.IsPassword, Checking = false });
            }
            if (!_pending) StopProbeTimer();
        }
        if (refreshFullscreen)
            Publish(Current with { IsFullscreen = ReadFullscreen() });
    }

    private void Publish(FocusInfo info, bool force = false)
    {
        var previous = Interlocked.Exchange(ref _current, info);
        if (force || previous != info)
            try { Changed?.Invoke(); } catch (Exception) { }
    }

    private static bool ReadPassword(nint focus, string windowClass, nint eventWindow, int objectId, int childId)
    {
        object? accessible = null;
        object? focused = null;
        try
        {
            if ((windowClass.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                 windowClass.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase) ||
                 windowClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) &&
                ((long)NativeMethods.GetWindowLongPtrW(focus, -16) & NativeMethods.ES_PASSWORD) != 0) return true;
            object child = 0;
            int result;
            if (eventWindow != 0)
                result = NativeMethods.AccessibleObjectFromEvent(eventWindow, unchecked((uint)objectId),
                    unchecked((uint)childId), out accessible, out child);
            else
            {
                var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                result = NativeMethods.AccessibleObjectFromWindow(focus, 0xFFFFFFFC, ref iid, out accessible);
            }
            if (result < 0 || accessible is not Accessibility.IAccessible acc) return false;
            if (eventWindow == 0)
            {
                focused = acc.accFocus;
                if (focused is Accessibility.IAccessible focusedAcc) acc = focusedAcc;
                else if (focused is int id) child = id;
            }
            return (Convert.ToInt32(acc.get_accState(child)) & NativeMethods.STATE_SYSTEM_PROTECTED) != 0;
        }
        catch (Exception) { return false; }
        finally
        {
            if (focused is not null && !ReferenceEquals(focused, accessible) && Marshal.IsComObject(focused)) Marshal.ReleaseComObject(focused);
            if (accessible is not null && Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible);
        }
    }

    private static string ReadProcessName(uint id)
    {
        nint process = 0;
        try
        {
            process = NativeMethods.OpenProcess(0x1000, false, id);
            if (process == 0) return "";
            var name = new StringBuilder(32768);
            var length = (uint)name.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(process, 0, name, ref length)
                ? Path.GetFileNameWithoutExtension(name.ToString()).ToLowerInvariant() : "";
        }
        catch (Exception) { return ""; }
        finally { if (process != 0) NativeMethods.CloseHandle(process); }
    }

    private static bool ReadFullscreen()
    {
        try { return NativeMethods.SHQueryUserNotificationState(out var state) >= 0 && state is 2 or 3 or 4; }
        catch (Exception) { return false; }
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
