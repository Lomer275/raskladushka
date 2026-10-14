using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Raskladushka.App.Native;

namespace Raskladushka.App.Shell;

public sealed class MemoryTrimmer : IDisposable
{
    private const int IdleMilliseconds = 60_000;
    private readonly Func<bool> _isBusy;
    private readonly FileLog _log;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private long _dueAt;
    private int _disposed;

    public MemoryTrimmer(Func<bool> isBusy, FileLog log)
    {
        _isBusy = isBusy;
        _log = log;
        _timer = new System.Threading.Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Touch()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _dueAt, Environment.TickCount64 + IdleMilliseconds);
        Schedule(IdleMilliseconds);
    }

    private void Schedule(int milliseconds)
    {
        try { _timer.Change(milliseconds, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                var dueAt = Volatile.Read(ref _dueAt);
                if (dueAt == 0) return;
                var remaining = dueAt - Environment.TickCount64;
                if (remaining > 0) { Schedule((int)remaining); return; }
                if (_isBusy()) { Touch(); return; }
                if (Interlocked.CompareExchange(ref _dueAt, 0, dueAt) != dueAt) return;
                TrimNow(_log);
            }
            catch (Exception error) { _log.Error("Memory trim failed", error); }
        }
    }

    public static (long Before, long After, long Private)? TrimNow(FileLog log)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var before = process.WorkingSet64;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (!NativeMethods.EmptyWorkingSet(process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process.Refresh();
            var after = process.WorkingSet64;
            var allocated = process.PrivateMemorySize64;
            log.Info($"Memory trim: working set {before / 1048576.0:F2} -> {after / 1048576.0:F2} MiB.");
            return (before, after, allocated);
        }
        catch (Exception error)
        {
            log.Error("Memory trim failed", error);
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }
}
