using System.Net;
using System.Net.Http;
using System.Text;
using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;
using Raskladushka.Core.Updates;

namespace Raskladushka.App.Shell;

public sealed class UpdateChecker : IDisposable
{
    private const int MaxResponseBytes = 1024 * 1024;
    private readonly Func<Settings> _settings;
    private readonly Action<string, NotifyAction> _notify;
    private readonly FileLog _log;
    private readonly string _stampPath;
    private readonly Version? _current = UpdateCheck.ParseCurrent(Application.ProductVersion);
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _stopToken;
    private readonly System.Threading.Timer _timer;
    private readonly object _lifecycle = new();
    private readonly HashSet<Version> _announced = [];
    private Uri? _latestPage;
    private bool _disposed;

    public Uri? LatestPage => Volatile.Read(ref _latestPage);

    public UpdateChecker(Func<Settings> settings, Action<string, NotifyAction> notify, FileLog log, string stampPath)
    {
        _settings = settings;
        _notify = notify;
        _log = log;
        _stampPath = stampPath;
        _stopToken = _shutdown.Token;
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        if (_current is not null) _client.DefaultRequestHeaders.UserAgent.ParseAdd($"Raskladushka/{_current}");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(manual: false), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            if (_current is null) { _log.Warn("Update check disabled: bad version."); return; }
            _timer.Change(TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));
        }
    }

    public Task CheckNowAsync() => CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        var entered = false;
        try
        {
            if (_stopToken.IsCancellationRequested) return;
            if (_current is null)
            {
                if (manual) _notify("Не удалось проверить обновления.", NotifyAction.None);
                return;
            }
            await _semaphore.WaitAsync(_stopToken).ConfigureAwait(false);
            entered = true;
            if (!manual && (!_settings().Updates.Notify ||
                !UpdateCheck.IsDue(UpdateStamp.Read(_stampPath), DateTimeOffset.UtcNow))) return;
            await CheckLockedAsync(manual).ConfigureAwait(false);
        }
        catch (Exception error) { _log.Error("Update check callback failed", error); }
        finally { if (entered) _semaphore.Release(); }
    }

    private async Task CheckLockedAsync(bool manual)
    {
        ReleaseInfo? latest = null;
        var result = "failed";
        try
        {
            // Headers must be observed before buffering so even an oversized response records the attempt.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _client.GetAsync(UpdateCheck.LatestUrl,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            try { UpdateStamp.Write(_stampPath, DateTimeOffset.UtcNow); }
            catch (Exception) { _log.Warn("Update check stamp write failed."); }
            if (response.StatusCode == HttpStatusCode.OK)
            {
                // ResponseHeadersRead bypasses HttpClient's buffer limit and body timeout.
                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    throw new HttpRequestException("Update response too large.");
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (body.Length + read > MaxResponseBytes) throw new HttpRequestException("Update response too large.");
                    body.Write(buffer, 0, read);
                }
                latest = UpdateCheck.Parse(Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
                if (latest is not null)
                {
                    result = UpdateCheck.IsNewer(latest, _current!) ? "newer" : "same";
                    if (result == "newer") Volatile.Write(ref _latestPage, latest.Page);
                }
            }
        }
        catch (Exception error) { _log.Error("Update check failed", error); }
        finally
        {
            _log.Info($"Update check: latest={latest?.Version.ToString() ?? "none"}; current={_current}; result={result}; manual={manual.ToString().ToLowerInvariant()}.");
        }

        if (_stopToken.IsCancellationRequested) return;
        if (result == "newer")
        {
            if (manual || (_settings().Updates.Notify && !_announced.Contains(latest!.Version)))
            {
                _notify($"Вышла версия {latest!.Version} — открыть страницу?", NotifyAction.OpenReleasePage);
                _announced.Add(latest.Version);
            }
        }
        else if (manual)
            _notify(result == "same" ? "Установлена последняя версия." : "Не удалось проверить обновления.", NotifyAction.None);
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            _shutdown.Cancel();
            _client.Dispose();
            _shutdown.Dispose();
        }
    }
}
