using System.Collections.Concurrent;
using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;

namespace Raskladushka.App.Dictation;

public sealed class DictationWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ConcurrentDictionary<Task, byte> _pending = new();
    private readonly Thread _thread;
    private readonly DictationMachine _machine;
    private readonly WorkerTimeProvider _time;
    private readonly IWhisperHost _whisper;
    private int _disposed;

    public DictationWorker(IAudioRecorder recorder, IWhisperHost whisper, ITextInserter inserter,
        IDictationUi ui, Func<DictationSettings> settings, Action<string> log, TimeProvider? time = null, IRemoteTranscriber? remote = null)
    {
        _whisper = whisper;
        _time = new WorkerTimeProvider(this, time ?? TimeProvider.System);
        _machine = new DictationMachine(recorder, whisper, inserter, ui, _time, settings, log, remote);
        _thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable()) action();
        }) { IsBackground = true, Name = "Raskladushka dictation" };
        _thread.Start();
    }

    public Task HotkeyDownAsync() => RunAsync(() => { _machine.OnHotkeyDown(); return Task.CompletedTask; });
    public Task HotkeyUpAsync() => RunAsync(_machine.OnHotkeyUpAsync);
    public Task CancelAsync() => RunAsync(() => { _machine.OnOtherKeyWhileHeld(); return Task.CompletedTask; });

    public Task SuspendAsync() => RunAsync(() => { _machine.Suspend(); return Task.CompletedTask; });
    public Task ResumeAsync() => RunAsync(() => { _machine.Resume(); return Task.CompletedTask; });

    private Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(() =>
        {
            try
            {
                var task = action();
                _pending.TryAdd(task, 0);
                _ = CompleteAsync(task, completion);
            }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new ObjectDisposedException(nameof(DictationWorker)));
        return completion.Task;
    }
    private async Task CompleteAsync(Task task, TaskCompletionSource completion)
    {
        try { await task.ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { _pending.TryRemove(task, out _); }
    }
    private bool Post(Action action)
    {
        try { _queue.Add(action); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _time.Dispose();
        Post(() =>
        {
            _machine.OnOtherKeyWhileHeld();
            if (_whisper is IDisposable disposable) disposable.Dispose();
            else _whisper.Stop();
        });
        _queue.CompleteAdding();
        _thread.Join();
        try { Task.WhenAll(_pending.Keys).GetAwaiter().GetResult(); }
        catch (Exception) { }
    }

    private sealed class WorkerTimeProvider(DictationWorker worker, TimeProvider inner) : TimeProvider, IDisposable
    {
        private readonly DictationWorker _worker = worker;
        private readonly TimeProvider _inner = inner;
        private readonly object _gate = new();
        private readonly HashSet<WorkerTimer> _timers = [];
        private bool _disposed;
        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();
        public override long GetTimestamp() => _inner.GetTimestamp();
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var timer = new WorkerTimer(this, callback, state);
                _timers.Add(timer);
                timer.Inner = _inner.CreateTimer(_ => _worker.Post(() => timer.Fire()), null, dueTime, period);
                return timer;
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var timer in _timers.ToArray()) timer.Dispose();
            }
        }
        private sealed class WorkerTimer(WorkerTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private readonly WorkerTimeProvider _owner = owner;
            private readonly TimerCallback _callback = callback;
            private readonly object? _state = state;
            private int _disposed;
            public ITimer? Inner { get; set; }
            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0) _callback(_state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Inner?.Change(dueTime, period) ?? false;
            public void Dispose()
            {
                lock (_owner._gate)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    Inner?.Dispose();
                    _owner._timers.Remove(this);
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
