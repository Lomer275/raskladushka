using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Raskladushka.App.Native;
using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;

namespace Raskladushka.App.Dictation;

public sealed class WhisperServerHost(Func<WhisperSettings> settings, Func<string> language) : IWhisperHost, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<WhisperSettings> _settings = settings;
    private readonly Func<string> _language = language;
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(120) };
    private Session? _session;
    private bool _disposed;

    internal int? ProcessId { get { lock (_gate) return _session?.Process.Id; } }

    public bool IsRunning
    {
        get { lock (_gate) return _session is { Ready: true } session && !session.Process.HasExited; }
    }

    public bool HasGpuBuild
    {
        get
        {
            var settings = _settings();
            return WhisperLayout.HasBuild(settings, WhisperMode.Gpu, File.Exists) &&
                WhisperLayout.ModelFor(settings, WhisperMode.Gpu, File.Exists) is not null;
        }
    }

    public bool HasCpuModel => WhisperLayout.HasCpuModel(_settings(), File.Exists);

    public Task StartAsync(WhisperMode mode, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (_session is { } previous)
            {
                if (!previous.Process.HasExited)
                {
                    if (previous.Mode != mode) throw new WhisperStartException("Перед сменой режима остановите Whisper.");
                    return previous.Startup;
                }
                Stop();
            }
            var launch = WhisperLayout.Resolve(_settings(), mode, _language(), Environment.ProcessorCount, File.Exists);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var info = new ProcessStartInfo(launch.Executable)
            {
                WorkingDirectory = launch.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.Environment["PATH"] = Path.GetDirectoryName(launch.Executable) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            foreach (var argument in launch.Arguments(port))
                info.ArgumentList.Add(argument);
            var session = new Session(new Process { StartInfo = info }, mode, port, ct);
            try
            {
                session.Job = NativeMethods.CreateJobObjectW(0, null);
                var limits = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = 0x2000;
                if (session.Job == 0 || !NativeMethods.SetInformationJobObject(session.Job, 9, ref limits,
                        (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                    throw new WhisperStartException("Не удалось создать объект задания Whisper.");
                if (!session.Process.Start()) throw new WhisperStartException("Не удалось запустить Whisper.");
                var assigned = NativeMethods.AssignProcessToJobObject(session.Job, session.Process.Handle);
                session.Output = DrainAsync(session.Process.StandardOutput, session, false);
                session.Errors = DrainAsync(session.Process.StandardError, session, true);
                if (!assigned)
                    throw new WhisperStartException("Не удалось включить Whisper в объект задания.");
                _session = session;
                session.Startup = WaitForReadyAsync(session);
                return session.Startup;
            }
            catch (Exception error)
            {
                session.Close();
                if (error is WhisperStartException) throw;
                throw new WhisperStartException($"Не удалось запустить Whisper, код 0x{error.HResult:X8}.");
            }
        }
    }

    private static async Task DrainAsync(StreamReader reader, Session session, bool errors)
    {
        // Bound individual lines too: malformed output must not grow memory without limit.
        var buffer = new char[4096];

        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (!errors) continue;
                lock (session.ErrorGate)
                {
                    if (session.Ready) { session.Line.Clear(); continue; }
                    for (var i = 0; i < count; i++)
                    {
                        if (buffer[i] == '\n')
                        {
                            session.Tail.Enqueue(session.Line.ToString().TrimEnd('\r'));
                            if (session.Tail.Count > 20) session.Tail.Dequeue();
                            session.Line.Clear();
                        }
                        else if (session.Line.Length < 4096) session.Line.Append(buffer[i]);
                    }
                }
            }
            lock (session.ErrorGate)
                if (!session.Ready && session.Line.Length > 0)
                {
                    session.Tail.Enqueue(session.Line.ToString());
                    if (session.Tail.Count > 20) session.Tail.Dequeue();
                }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task WaitForReadyAsync(Session session)
    {
        var ct = session.Cancellation.Token;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (session.Process.HasExited)
            {
                await session.Errors.ConfigureAwait(false);
                lock (session.ErrorGate)
                    throw new WhisperStartException("Whisper завершился до готовности. " + string.Join(Environment.NewLine, session.Tail));
            }
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, session.Port, ct).ConfigureAwait(false);
                lock (session.ErrorGate)
                {
                    session.Ready = true;
                    session.Tail.Clear();
                    session.Line.Clear();
                }
                return;
            }
            catch (SocketException) { }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> TranscribeAsync(AudioClip clip, string language, string prompt, CancellationToken ct)
    {
        Session session;
        lock (_gate)
        {
            if (!IsRunning) throw new InvalidOperationException("Whisper не запущен.");
            session = _session!;
        }
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token);
            using var form = new MultipartFormDataContent();
            using var file = new ByteArrayContent(clip.Wav);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "dictation.wav");
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent("0.0"), "temperature");
            form.Add(new StringContent(language), "language");
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
            using var response = await _http.PostAsync($"http://127.0.0.1:{session.Port}/inference", form, cancellation.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token).ConfigureAwait(false));
            if (json.RootElement.TryGetProperty("error", out _) ||
                !json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException();
            return text.GetString()!;
        }
        catch (Exception)
        {
            // Neither response bodies nor parser exceptions may expose the transcript.
            throw new InvalidOperationException("Не удалось выполнить запрос распознавания Whisper.");
        }
    }

    public void Stop()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        session?.Close();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _http.Dispose();
    }

    private sealed class Session(Process process, WhisperMode mode, int port, CancellationToken ct)
    {
        public Process Process { get; } = process;
        public WhisperMode Mode { get; } = mode;
        public int Port { get; } = port;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public nint Job { get; set; }
        public volatile bool Ready;
        public object ErrorGate { get; } = new();
        public Queue<string> Tail { get; } = new();
        public System.Text.StringBuilder Line { get; } = new();
        public Task Output { get; set; } = Task.CompletedTask;
        public Task Errors { get; set; } = Task.CompletedTask;
        public Task Startup { get; set; } = Task.CompletedTask;
        public void Close()
        {
            Cancellation.Cancel();
            try
            {
                if (Job != 0) { NativeMethods.CloseHandle(Job); Job = 0; }
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                Process.WaitForExit(3000);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                // Let pending reads/polls unwind before disposing their handles.
                _ = CleanupAsync();
            }
        }
        private async Task CleanupAsync()
        {
            try { await Task.WhenAll(Startup, Output, Errors).ConfigureAwait(false); }
            catch (Exception) { }
            finally { Process.Dispose(); Cancellation.Dispose(); }
        }
    }
}
