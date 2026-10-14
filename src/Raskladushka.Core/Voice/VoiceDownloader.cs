using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Raskladushka.Core.Voice;

public sealed class VoiceDownloader(HttpClient http, TimeProvider time)
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public async Task<string> DownloadAsync(VoicePackage package, string downloadsDir,
        IProgress<VoiceProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(downloadsDir);
        var final = Path.Combine(downloadsDir, package.File);
        var part = final + ".part";
        if (File.Exists(final))
        {
            if (await MatchesAsync(final, package, ct))
            {
                progress?.Report(new(package.Id, package.Size, package.Size));
                return final;
            }
            File.Delete(final);
        }

        var restarted = false;
        if (File.Exists(part) && new FileInfo(part).Length >= package.Size)
        {
            File.Delete(part);
            restarted = true;
        }
        while (true)
        {
            var hasPart = File.Exists(part);
            var offset = hasPart ? new FileInfo(part).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, package.Url);
            if (hasPart) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await NetworkAsync(token =>
                http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token), package, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && !restarted)
            {
                File.Delete(part);
                restarted = true;
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw Network(package, $"HTTP {(int)response.StatusCode}");
            if (response.StatusCode != HttpStatusCode.PartialContent) offset = 0;
            using var body = await NetworkAsync(token => response.Content.ReadAsStreamAsync(token), package, ct);
            var overflow = false;
            var done = offset;
            try
            {
                await using var output = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 65536, useAsync: true);
                var buffer = new byte[65536];
                var lastReported = offset;
                while (true)
                {
                    var remaining = package.Size - done;
                    var readSize = remaining < buffer.Length ? (int)remaining + 1 : buffer.Length;
                    if (done < package.Size)
                        readSize = (int)Math.Min(readSize, 1024 * 1024 - (done - lastReported));
                    var count = await NetworkAsync(token => body.ReadAsync(buffer.AsMemory(0, readSize), token)
                        .AsTask(), package, ct);
                    if (count == 0) break;
                    if (count > package.Size - done)
                    {
                        overflow = true;
                        break;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    done += count;
                    if (done - lastReported >= 1024 * 1024 && done < package.Size)
                    {
                        progress?.Report(new(package.Id, done, package.Size));
                        lastReported = done;
                    }
                }
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 0x70 or 0x27)
            {
                throw new VoiceInstallException(VoiceFailure.DiskSpace, "Не хватает места на диске.");
            }
            if (overflow)
            {
                File.Delete(part);
                throw Network(package, "сервер прислал больше данных, чем ожидалось");
            }
            if (done != package.Size) throw Network(package, "загрузка оборвалась");
            if (!await MatchesAsync(part, package, ct))
            {
                File.Delete(part);
                throw new VoiceInstallException(VoiceFailure.Checksum, $"Не совпала контрольная сумма {package.File}.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(part, final, overwrite: true);
            progress?.Report(new(package.Id, package.Size, package.Size));
            return final;
        }
    }

    internal static async Task<bool> MatchesAsync(string path, VoicePackage package, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path) || new FileInfo(path).Length != package.Size) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<T> NetworkAsync<T>(Func<CancellationToken, Task<T>> action,
        VoicePackage package, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(StallTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await action(linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException)
        {
            throw Network(package, "сервер не отвечает");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            throw Network(package, "соединение прервано");
        }
    }

    private static VoiceInstallException Network(VoicePackage package, string reason) =>
        new(VoiceFailure.Network, $"Не удалось скачать {package.File}: {reason}.");
}
