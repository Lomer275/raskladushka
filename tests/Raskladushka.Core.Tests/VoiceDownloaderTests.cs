using System.Net;
using Raskladushka.Core.Voice;

namespace Raskladushka.Core.Tests;

public class VoiceDownloaderTests
{
    [Fact]
    public async Task EmptyPartialUsesRangeStartingAtZero()
    {
        using var kit = new VoiceTestKit();
        kit.Seed(kit.Part, []);
        await kit.Downloader.DownloadAsync(kit.Model, kit.Downloads, null, TestContext.Current.CancellationToken);
        Assert.Equal("bytes=0-", Assert.Single(kit.Handler.Requests).Range);
    }

    [Theory]
    [InlineData(0x70)]
    [InlineData(0x27)]
    [InlineData(5)]
    public async Task ReadErrorsRemainNetworkFailuresEvenWithDiskErrorCode(int error)
    {
        using var kit = new VoiceTestKit();
        kit.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new VoiceBrokenStream(error)) });
        var ex = await Assert.ThrowsAsync<VoiceInstallException>(() => kit.Downloader.DownloadAsync(
            kit.Model, kit.Downloads, null, TestContext.Current.CancellationToken));
        Assert.Equal(VoiceFailure.Network, ex.Failure);
        Assert.DoesNotContain("private path", ex.Message);
        Assert.Equal(1, new FileInfo(kit.Part).Length);
    }

    [Fact]
    public async Task CancelledHashVerificationPreservesCachedFile()
    {
        using var kit = new VoiceTestKit();
        var path = Path.Combine(kit.Downloads, kit.Model.File);
        kit.Seed(path, kit.Data[kit.Model.File]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VoiceDownloader.MatchesAsync(path, kit.Model, cts.Token));
        Assert.True(File.Exists(path));
        Assert.Empty(kit.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumesOrRestartsWhenRangeIgnored(bool ignoreRange)
    {
        using var kit = new VoiceTestKit();
        var bytes = kit.Data[kit.Model.File];
        kit.Seed(kit.Part, bytes[..(bytes.Length / 2)]);
        if (ignoreRange) kit.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) });
        var reports = new List<VoiceProgress>();
        var path = await kit.Downloader.DownloadAsync(kit.Model, kit.Downloads, new VoiceProgressSink(reports.Add), TestContext.Current.CancellationToken);
        Assert.Equal($"bytes={bytes.Length / 2}-", Assert.Single(kit.Handler.Requests).Range);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(new(kit.Model.Id, bytes.Length, bytes.Length), reports[^1]);
    }

    [Theory]
    [InlineData(416)]
    [InlineData(4096)]
    [InlineData(4097)]
    public async Task InvalidPartialRestartsOnce(int mode)
    {
        using var kit = new VoiceTestKit();
        kit.Seed(kit.Part, new byte[mode == 416 ? 10 : mode]);
        var normal = kit.Handler.Respond;
        kit.Handler.Respond = (request, ct) => kit.Handler.Requests.Count == 1 && mode == 416
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)) : normal(request, ct);
        await kit.Downloader.DownloadAsync(kit.Model, kit.Downloads, null, TestContext.Current.CancellationToken);
        Assert.Equal(mode == 416 ? 2 : 1, kit.Handler.Requests.Count);
        Assert.Null(kit.Handler.Requests[^1].Range);
    }

    [Fact]
    public async Task Repeated416Stops()
    {
        using var kit = new VoiceTestKit();
        kit.Seed(kit.Part, [1]);
        kit.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        var ex = await Assert.ThrowsAsync<VoiceInstallException>(() => kit.Downloader.DownloadAsync(kit.Model, kit.Downloads, null, TestContext.Current.CancellationToken));
        Assert.Equal(VoiceFailure.Network, ex.Failure);
        Assert.Contains("416", ex.Message);
        Assert.Equal(2, kit.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("checksum", VoiceFailure.Checksum, false)]
    [InlineData("html", VoiceFailure.Network, true)]
    [InlineData("extra", VoiceFailure.Network, false)]
    [InlineData("short", VoiceFailure.Network, true)]
    public async Task BadBodyIsRejected(string mode, VoiceFailure failure, bool partRemains)
    {
        using var kit = new VoiceTestKit();
        var bytes = kit.Data[kit.Model.File].ToArray();
        if (mode == "checksum") bytes[0] ^= 1;
        if (mode == "html") bytes = System.Text.Encoding.UTF8.GetBytes("<html>" + new string('x', 2035) + "</html>");
        if (mode == "extra") bytes = [.. bytes, 0];
        if (mode == "short") bytes = bytes[..100];
        kit.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) });
        var ex = await Assert.ThrowsAsync<VoiceInstallException>(() => kit.Install(VoiceVariant.CpuModel, ct: TestContext.Current.CancellationToken));
        Assert.Equal(failure, ex.Failure);
        Assert.Contains(kit.Model.File, ex.Message);
        Assert.Equal(partRemains, File.Exists(kit.Part));
        Assert.False(File.Exists(Path.Combine(kit.WhisperDir, kit.Model.Target)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedFileVerifiedBeforeUse(bool valid)
    {
        using var kit = new VoiceTestKit();
        var bytes = kit.Data[kit.Model.File].ToArray();
        if (!valid) bytes[0] ^= 1;
        kit.Seed(Path.Combine(kit.Downloads, kit.Model.File), bytes);
        await kit.Install(VoiceVariant.CpuModel, ct: TestContext.Current.CancellationToken);
        Assert.Equal(valid ? 0 : 1, kit.Handler.Requests.Count);
        Assert.Equal(kit.Data[kit.Model.File], File.ReadAllBytes(Path.Combine(kit.WhisperDir, kit.Model.Target)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledBodyTimesOutOrCancels(bool cancel)
    {
        using var kit = new VoiceTestKit();
        using var cts = new CancellationTokenSource();
        var stream = new VoiceStallingStream();
        kit.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) });
        var task = kit.Downloader.DownloadAsync(kit.Model, kit.Downloads, null, cts.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (cancel) cts.Cancel();
        else kit.Time.Advance(TimeSpan.FromSeconds(31));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.Equal(VoiceFailure.Network, (await Assert.ThrowsAsync<VoiceInstallException>(() => task)).Failure);
        Assert.Equal(1, new FileInfo(kit.Part).Length);
    }

    [Fact]
    public async Task StalledHeadersTimeOut()
    {
        using var kit = new VoiceTestKit();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Handler.Respond = async (_, ct) =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var task = kit.Downloader.DownloadAsync(kit.Model, kit.Downloads, null, TestContext.Current.CancellationToken);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        kit.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(VoiceFailure.Network, (await Assert.ThrowsAsync<VoiceInstallException>(() => task)).Failure);
    }

    [Theory]
    [InlineData("404")]
    [InlineData("http")]
    [InlineData("io")]
    public async Task NetworkErrorsNameFile(string mode)
    {
        using var kit = new VoiceTestKit();
        kit.Handler.Respond = (_, _) => mode switch
        {
            "http" => throw new HttpRequestException("secret path"),
            "io" => throw new IOException("secret path"),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        };
        var ex = await Assert.ThrowsAsync<VoiceInstallException>(() => kit.Install(VoiceVariant.CpuModel, ct: TestContext.Current.CancellationToken));
        Assert.Equal(VoiceFailure.Network, ex.Failure);
        Assert.Contains(kit.Model.File, ex.Message);
        Assert.DoesNotContain("secret path", ex.Message);
        if (mode == "404") Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task ProgressIncludesResumedBytesAndReportsEveryMegabyte()
    {
        using var kit = new VoiceTestKit();
        var bytes = new byte[4 * 1024 * 1024 + 17];
        kit.Data[kit.Model.File] = bytes;
        kit.RebuildCatalog();
        kit.Seed(kit.Part, bytes[..(512 * 1024)]);
        var reports = new List<VoiceProgress>();
        await kit.Downloader.DownloadAsync(kit.Model, kit.Downloads, new VoiceProgressSink(reports.Add), TestContext.Current.CancellationToken);
        long previous = 512 * 1024;
        foreach (var report in reports)
        {
            Assert.InRange(report.Done - previous, 1, 1024 * 1024);
            Assert.Equal(bytes.Length, report.Total);
            previous = report.Done;
        }
        Assert.Equal(bytes.Length, previous);
    }
}
