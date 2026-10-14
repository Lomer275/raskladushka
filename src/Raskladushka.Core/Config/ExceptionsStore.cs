using Raskladushka.Core.Typing;

namespace Raskladushka.Core.Config;

public sealed class ExceptionsStore : IExceptionSink, IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly HashSet<string> _words = new(StringComparer.Ordinal);
    private Task _writer = Task.CompletedTask;
    private bool _writing;
    private bool _dirty;
    private bool _disposed;

    public ExceptionsStore(string path)
    {
        _path = path;
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var word = Normalize(line);
                if (word.Length > 0 && !word.StartsWith('#')) _words.Add(word);
            }
        }
        catch (Exception)
        {
            // Unavailable storage must not disable in-memory exceptions.
        }
    }

    public bool Contains(string lowerWord)
    {
        lock (_gate) return _words.Contains(Normalize(lowerWord));
    }

    public void Add(string lowerWord)
    {
        var word = Normalize(lowerWord);
        if (word.Length == 0) return;
        lock (_gate)
        {
            if (_disposed || !_words.Add(word)) return;
            QueueWrite();
        }
    }

    public bool Remove(string lowerWord)
    {
        lock (_gate)
        {
            if (_disposed || !_words.Remove(Normalize(lowerWord))) return false;
            QueueWrite();
            return true;
        }
    }

    public void Flush()
    {
        Task writer;
        lock (_gate) writer = _writer;
        writer.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Flush();
    }

    private static string Normalize(string word) => word.Trim().ToLowerInvariant();

    private void QueueWrite()
    {
        _dirty = true;
        if (_writing) return;
        _writing = true;
        _writer = Task.Run(WritePending);
    }

    private void WritePending()
    {
        while (true)
        {
            string[] snapshot;
            lock (_gate)
            {
                if (!_dirty)
                {
                    _writing = false;
                    return;
                }
                snapshot = _words.ToArray();
                _dirty = false;
            }

            try
            {
                // Never hold the memory lock while waiting for storage.
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllLines(_path, snapshot);
            }
            catch (Exception)
            {
                // Keep memory usable even if the directory is not writable.
            }
        }
    }
}
