using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Raskladushka.Core.Voice;

namespace Raskladushka.Core.Tests;

internal sealed class VoiceTestKit : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "voice-" + Guid.NewGuid().ToString("N"));
    public string WhisperDir { get; }
    public string RuntimeDir { get; }
    public Dictionary<string, byte[]> Data { get; } = new();
    public FakeTimeProvider Time { get; } = new();
    public VoiceHandler Handler { get; } = new();
    public HttpClient Http { get; }
    public VoiceCatalog Catalog { get; private set; } = null!;
    public string Json { get; private set; } = "";

    public VoiceTestKit()
    {
        WhisperDir = Path.Combine(Root, "Иван Петров", "голос");
        RuntimeDir = Path.Combine(Root, "vcrt");
        Directory.CreateDirectory(RuntimeDir);
        foreach (var file in RuntimeCopier.Files) File.WriteAllText(Path.Combine(RuntimeDir, file), file);
        Data["cpu.zip"] = Zip("Release/whisper-server.exe", "Release/a.dll", "Release/bench.exe");
        Data["cuda.zip"] = Zip("Release/whisper-server.exe", "Release/cuda.dll");
        Data["ggml-small.bin"] = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        Data["ggml-large.bin"] = [1, 2, 3, 4, 5];
        RebuildCatalog();
        Handler.Respond = (request, _) =>
        {
            var bytes = Data[request.RequestUri!.Segments[^1]];
            var offset = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            return Task.FromResult(new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes[offset..]) });
        };
        Http = new HttpClient(Handler);
    }

    public void RebuildCatalog()
    {
        object Package(string id, string kind, string target, string file) => new
        {
            id, kind, target, file, url = "https://example.test/" + file,
            size = Data[file].Length, unpackedSize = kind == "build" ? 10000 : 0, sha256 = Hash(Data[file])
        };
        Json = JsonSerializer.Serialize(new
        {
            whisperRelease = "test",
            packages = new[] { Package("build-cpu", "build", "cpu", "cpu.zip"),
                Package("build-cuda", "build", "cuda", "cuda.zip"),
                Package("model-cpu", "model", "ggml-small.bin", "ggml-small.bin"),
                Package("model-gpu", "model", "ggml-large.bin", "ggml-large.bin") },
            variants = new { gpu = new[] { "build-cuda", "build-cpu", "model-gpu" },
                cpu = new[] { "build-cpu", "model-cpu" }, cpuModel = new[] { "model-cpu" } }
        });
        Catalog = VoiceCatalog.Parse(Json);
    }

    public VoiceDownloader Downloader => new(Http, Time);
    public VoiceInstaller Installer(Func<string, long>? free = null) => new(Catalog, Downloader, free ?? (_ => long.MaxValue));
    public VoicePackage Model => Catalog.Packages(VoiceVariant.CpuModel).Single();
    public string Downloads => Path.Combine(WhisperDir, "downloads");
    public string Part => Path.Combine(Downloads, Model.File + ".part");
    public Task Install(VoiceVariant variant = VoiceVariant.Cpu, IProgress<VoiceProgress>? progress = null,
        CancellationToken ct = default) => Installer().InstallAsync(variant, WhisperDir, RuntimeDir, progress, ct);
    public void Seed(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static byte[] Zip(params string[] names)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in names)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        return buffer.ToArray();
    }
    public void Dispose()
    {
        Http.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class VoiceHandler : HttpMessageHandler
{
    public List<(Uri Url, string? Range)> Requests { get; } = [];
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = null!;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!, request.Headers.Range?.ToString()));
        return Respond(request, cancellationToken);
    }
}

internal sealed class VoiceProgressSink(Action<VoiceProgress> report) : IProgress<VoiceProgress>
{
    public void Report(VoiceProgress value) => report(value);
}

internal sealed class VoiceStallingStream : MemoryStream
{
    private bool sent;
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!sent)
        {
            sent = true;
            buffer.Span[0] = 42;
            return 1;
        }
        Waiting.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}

internal sealed class VoiceBrokenStream(int error) : MemoryStream
{
    private bool sent;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (sent) throw new IOException("private path", error);
        sent = true;
        buffer.Span[0] = 42;
        return ValueTask.FromResult(1);
    }
}
