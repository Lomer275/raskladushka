using System.Globalization;

namespace Raskladushka.Core.Voice;

public sealed class VoiceInstaller(VoiceCatalog catalog, VoiceDownloader downloader, Func<string, long> freeBytes)
{
    public const long Margin = 100L * 1024 * 1024;

    public async Task<bool> IsInstalledAsync(VoiceVariant variant, string whisperDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var package in catalog.Packages(variant))
            if (!await InstalledAsync(package, whisperDir, ct).ConfigureAwait(false)) return false;
        return true;
    }

    public long RequiredFreeBytes(VoiceVariant variant, string whisperDir) =>
        RequiredFreeBytesAsync(variant, whisperDir, CancellationToken.None).GetAwaiter().GetResult();

    private async Task<long> RequiredFreeBytesAsync(VoiceVariant variant, string whisperDir, CancellationToken ct)
    {
        long required = Margin;
        foreach (var package in catalog.Packages(variant))
        {
            if (await InstalledAsync(package, whisperDir, ct).ConfigureAwait(false)) continue;
            var download = Path.Combine(whisperDir, "downloads", package.File);
            long downloaded = 0;
            if (File.Exists(download) && new FileInfo(download).Length == package.Size)
                downloaded = package.Size;
            else if (File.Exists(download + ".part"))
            {
                var length = new FileInfo(download + ".part").Length;
                if (length < package.Size) downloaded = length;
            }
            required = checked(required + package.Size - downloaded +
                (package.Kind == PackageKind.Build ? package.UnpackedSize : 0));
        }
        return required;
    }

    public async Task InstallAsync(VoiceVariant variant, string whisperDir, string runtimeDir,
        IProgress<VoiceProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var packages = catalog.Packages(variant);
        foreach (var package in packages.Where(p => p.Kind == PackageKind.Build))
        {
            DeleteDirectory(Path.Combine(whisperDir, package.Target + ".new"));
            DeleteDirectory(Path.Combine(whisperDir, package.Target + ".old"));
        }
        var pending = new List<VoicePackage>();
        foreach (var package in packages)
        {
            if (await InstalledAsync(package, whisperDir, ct))
                progress?.Report(new(package.Id, package.Size, package.Size));
            else pending.Add(package);
        }
        if (pending.Any(p => p.Kind == PackageKind.Build)) RuntimeCopier.Check(runtimeDir);
        var required = await RequiredFreeBytesAsync(variant, whisperDir, ct);
        var available = freeBytes(whisperDir);
        if (available < required)
            throw new VoiceInstallException(VoiceFailure.DiskSpace, string.Format(
                CultureInfo.GetCultureInfo("ru-RU"),
                "Не хватает места на диске: нужно {0:0.0} ГБ, свободно {1:0.0} ГБ.",
                required / (double)(1L << 30), available / (double)(1L << 30)));
        var downloads = Path.Combine(whisperDir, "downloads");
        foreach (var package in pending)
        {
            ct.ThrowIfCancellationRequested();
            var downloaded = await downloader.DownloadAsync(package, downloads, progress, ct);
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(whisperDir, package.Target);
            if (package.Kind == PackageKind.Model)
                await RetryAsync(() => File.Move(downloaded, target, overwrite: true), package.Target, ct);
            else
            {
                var staging = target + ".new";
                var old = target + ".old";
                try
                {
                    BuildExtractor.Extract(downloaded, staging, ct);
                    RuntimeCopier.CopyInto(runtimeDir, staging);
                    await File.WriteAllTextAsync(Path.Combine(staging, ".package"), package.Sha256, ct);
                    ct.ThrowIfCancellationRequested();
                    var hadOld = Directory.Exists(target);
                    if (hadOld) await RetryAsync(() => Directory.Move(target, old), package.Target, ct);
                    try
                    {
                        // Once the old build is moved, complete or roll back even if cancellation arrives.
                        await RetryAsync(() => Directory.Move(staging, target), package.Target, CancellationToken.None);
                    }
                    catch
                    {
                        if (hadOld)
                            await RetryAsync(() => Directory.Move(old, target), package.Target, CancellationToken.None);
                        throw;
                    }
                    TryDeleteDirectory(old);
                    File.Delete(downloaded);
                }
                finally
                {
                    TryDeleteDirectory(staging);
                }
            }
        }
        if (Directory.Exists(downloads) && !Directory.EnumerateFileSystemEntries(downloads).Any())
            Directory.Delete(downloads);
    }

    private static async Task<bool> InstalledAsync(VoicePackage package, string whisperDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = Path.Combine(whisperDir, package.Target);
        if (package.Kind == PackageKind.Model)
            return await VoiceDownloader.MatchesAsync(target, package, ct).ConfigureAwait(false);
        var marker = Path.Combine(target, ".package");
        return File.Exists(Path.Combine(target, "whisper-server.exe")) && File.Exists(marker) &&
            (await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false)).Trim()
                .Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RetryAsync(Action action, string target, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    throw new VoiceInstallException(VoiceFailure.Extraction,
                        $"Не удалось заменить {target}: файлы заняты другой программой. Повторите позже.");
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { DeleteDirectory(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
