using System.Threading.Channels;
using System.Text;

namespace Raskladushka.App.Shell;

public sealed class FileLog : IDisposable
{
    private readonly Channel<(string? Line, TaskCompletionSource? Flush, Action? Work)> _queue =
        Channel.CreateUnbounded<(string?, TaskCompletionSource?, Action?)>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly string _fileName;
    private readonly Thread _thread;
    private int _disposed;
    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raskladushka", "logs");
    public string FilePath => Path.Combine(DirectoryPath, _fileName);

    public FileLog(string fileName = "raskladushka.log")
    {
        _fileName = fileName;
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "Raskladushka log" };
        _thread.Start();
    }

    public void Info(string message) => Enqueue("INFO", message);
    public void Warn(string message) => Enqueue("WARN", message);
    public void Error(string message) => Enqueue("ERROR", message);
    public void Error(string context, Exception error) =>
        Error($"{context}: {error.GetType().Name}, 0x{error.HResult:X8}");

    private void Enqueue(string level, string message)
    {
        try { _queue.Writer.TryWrite(($"{DateTimeOffset.Now:O} {level} {message.Replace('\r', ' ').Replace('\n', ' ')}", null, null)); }
        catch (Exception) { }
    }

    public void Enqueue(Action work) => _queue.Writer.TryWrite((null, null, work));

    public void Flush()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite((null, completion, null))) { _thread.Join(); return; }
        completion.Task.GetAwaiter().GetResult();
    }

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        var disabled = false;
        long size = 0;
        while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        while (_queue.Reader.TryRead(out var item))
        {
            try
            {
                if (item.Work is { } work)
                {
                    try { work(); } catch (Exception) { }
                }
                if (!disabled)
                {
                    if (item.Line is not null)
                    {
                        if (writer is null)
                        {
                            Directory.CreateDirectory(DirectoryPath);
                            writer = Open();
                            size = writer.BaseStream.Length;
                        }
                        var bytes = Encoding.UTF8.GetByteCount(item.Line + Environment.NewLine);
                        if (size + bytes > 1024 * 1024)
                        {
                            writer.Dispose();
                            writer = null;
                            File.Delete(FilePath + ".2");
                            if (File.Exists(FilePath + ".1")) File.Move(FilePath + ".1", FilePath + ".2");
                            File.Move(FilePath, FilePath + ".1");
                            writer = Open();
                            size = 0;
                        }
                        writer.WriteLine(item.Line);
                        writer.Flush();
                        size += bytes;
                    }
                    else writer?.Flush();
                }
            }
            catch (Exception)
            {
                disabled = true;
                try { writer?.Dispose(); } catch (Exception) { }
                writer = null;
            }
            finally { item.Flush?.TrySetResult(); }
        }
        try { writer?.Dispose(); } catch (Exception) { }

        StreamWriter Open() => new(new FileStream(FilePath, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _thread.Join();
    }
}
