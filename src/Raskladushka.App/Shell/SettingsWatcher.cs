using Raskladushka.Core.Config;

namespace Raskladushka.App.Shell;

public sealed class SettingsWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<Settings> _current;
    private readonly Action<Settings> _apply;
    private readonly Action<string> _notify;
    private readonly FileLog _log;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _timer;
    private bool _retry;
    private bool _disposed;
    private long _reloadAt;

    public SettingsWatcher(string path, Func<Settings> current, Action<Settings> apply,
        Action<string> notify, FileLog log)
    {
        _path = path;
        _current = current;
        _apply = apply;
        _notify = notify;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _timer = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Deleted += Changed;
        _watcher.Renamed += Changed;
        _watcher.Error += (_, _) => { _log.Warn("Settings watcher error."); Schedule(); };
        try { _watcher.EnableRaisingEvents = true; }
        catch { _watcher.Dispose(); _timer.Dispose(); throw; }
    }

    private void Changed(object sender, FileSystemEventArgs e) => Schedule();
    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _retry = false;
            _reloadAt = Environment.TickCount64 + 300;
            _timer.Change(300, Timeout.Infinite);
        }
    }

    private void Reload()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var remaining = _reloadAt - Environment.TickCount64;
            if (remaining > 0) { _timer.Change((int)remaining, Timeout.Infinite); return; }
            try
            {
                var loaded = SettingsStore.Load(_path, _current());
                if (loaded.Error is not null)
                {
                    if (!_retry)
                    {
                        _retry = true;
                        _reloadAt = Environment.TickCount64 + 300;
                        _timer.Change(300, Timeout.Infinite);
                        return;
                    }
                    _log.Warn("Settings reload failed; previous settings retained.");
                    _notify("Ошибка в файле настроек, действуют прежние");
                    return;
                }
                _apply(loaded.Settings);
                _log.Info("Settings reloaded.");
            }
            catch (Exception error) { _log.Error("Settings reload callback failed", error); }
        }
    }

    public void Update(Func<Settings, Settings> change)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _current();
            var updated = change(current);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Atomic replacement prevents the watcher from reading our partial write.
                File.WriteAllText(temporary, SettingsStore.ToJson(updated));
                File.Move(temporary, _path, overwrite: true);
                _apply(updated);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watcher.Dispose();
            _timer.Dispose();
        }
    }
}
