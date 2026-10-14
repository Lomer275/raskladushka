using System.ComponentModel;
using System.Diagnostics;

namespace Raskladushka.App.Shell;

internal sealed class ExitSignal : IDisposable
{
    public const string EventName = @"Local\Raskladushka.Exit";
    public const string MutexName = @"Local\Raskladushka";

    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _wait;
    private readonly object _gate = new();
    private bool _disposed;

    public ExitSignal(SynchronizationContext context, Action onSignal)
    {
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) =>
        {
            lock (_gate)
            {
                if (!_disposed) context.Post(_ => onSignal(), null);
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _wait.Unregister(null);
            _event.Dispose();
        }
    }

    public static int RequestExit(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        int Remaining() => (int)Math.Clamp((timeout - elapsed.Elapsed).TotalMilliseconds, 0, int.MaxValue);

        while (true)
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var signal))
            {
                using (signal) signal.Set();
                break;
            }
            if (!Mutex.TryOpenExisting(MutexName, out var startingMutex)) return 0;
            startingMutex.Dispose();
            var remaining = Remaining();
            if (remaining == 0) return 1;
            Thread.Sleep(Math.Min(100, remaining));
        }

        if (Mutex.TryOpenExisting(MutexName, out var mutex))
        {
            using (mutex)
            {
                try
                {
                    if (!mutex.WaitOne(Remaining())) return 1;
                }
                catch (AbandonedMutexException) { }
                mutex.ReleaseMutex();
            }
        }

        using var current = Process.GetCurrentProcess();
        var sessionId = current.SessionId;
        var processes = Process.GetProcessesByName("Raskladushka");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    // Only an instance started before this command can be the one being closed;
                    // a later --exit would otherwise wait for us while we wait for it.
                    if (process.Id == current.Id || process.SessionId != sessionId ||
                        process.StartTime >= current.StartTime) continue;
                    if (!process.WaitForExit(Remaining())) return 1;
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return 0;
    }
}
