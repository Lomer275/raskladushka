using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;

namespace Raskladushka.Core.Tests;

public sealed class RemoteWhisperClientTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Protector(string? plain = "test-key-1") : ISecretProtector
    {
        public string Protect(string value) => "test-key-protected";
        public string? Unprotect(string value) => plain;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return respond(request, ct);
        }
    }

    private static HttpResponseMessage Response(int status = 200, string body = "{\"text\":\" result \"}") =>
        new((HttpStatusCode)status) { Content = new StringContent(body) };

    private static RemoteWhisperClient Client(HttpMessageHandler handler, string key = "test-key-protected",
        ISecretProtector? protector = null, string url = "http://127.0.0.1:1/exact/inference?x=1", TimeProvider? time = null) =>
        new(handler, () => new() { Url = url, KeyProtected = key }, protector ?? new Protector(), time ?? TimeProvider.System);

    [Fact]
    public async Task C1_ExactMultipartRequestAndUnchangedText()
    {
        var time = new FakeTimeProvider();
        var uploaded = 0;
        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:1/exact/inference?x=1", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-key-1", request.Headers.Authorization!.ToString());
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal(5, parts.Count);
            var file = parts["file"];
            Assert.Equal("audio.ogg", file.Headers.ContentDisposition!.FileName!.Trim('"'));
            Assert.Equal("audio/ogg", file.Headers.ContentType!.MediaType);
            var data = await file.ReadAsByteArrayAsync(ct);
            uploaded = data.Length;
            Assert.True(data.AsSpan(0, 4).SequenceEqual("OggS"u8));
            Assert.Equal("json", await parts["response_format"].ReadAsStringAsync(ct));
            Assert.Equal("0.0", await parts["temperature"].ReadAsStringAsync(ct));
            Assert.Equal("ru", await parts["language"].ReadAsStringAsync(ct));
            Assert.Equal("terms", await parts["prompt"].ReadAsStringAsync(ct));
            time.Advance(TimeSpan.FromMilliseconds(123));
            return Response();
        });
        using var client = Client(handler, time: time);
        var result = await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "terms", Limit, Ct);
        Assert.Equal(RemoteOutcome.Ok, result.Outcome);
        Assert.Equal(" result ", result.Text);
        Assert.Equal(200, result.Status);
        Assert.Equal(uploaded, result.UploadBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(123), result.Elapsed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task C2_EmptyKeyOmitsAuthorizationAndEmptyPromptIsOmitted()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain(Assert.IsType<MultipartFormDataContent>(request.Content),
                p => p.Headers.ContentDisposition!.Name!.Trim('"') == "prompt");
            return Task.FromResult(Response());
        });
        using var client = Client(handler, key: "");
        Assert.Equal(RemoteOutcome.Ok, (await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "auto", "", Limit, Ct)).Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task C3_UndecryptableKeyRejectsWithoutRequest()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        using var client = Client(handler, protector: new Protector(null));
        var result = await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", Limit, Ct);
        Assert.Equal(RemoteOutcome.KeyRejected, result.Outcome);
        Assert.Null(result.Text);
        Assert.Null(result.Status);
        Assert.Equal(0, result.UploadBytes);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(401, RemoteOutcome.KeyRejected)]
    [InlineData(403, RemoteOutcome.KeyRejected)]
    [InlineData(429, RemoteOutcome.Busy)]
    [InlineData(503, RemoteOutcome.Busy)]
    [InlineData(404, RemoteOutcome.Failed)]
    [InlineData(500, RemoteOutcome.Failed)]
    public async Task C4_StatusClassification(int status, RemoteOutcome expected)
    {
        using var client = Client(new Handler((_, _) => Task.FromResult(Response(status))));
        var result = await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", Limit, Ct);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Text);
        Assert.True(result.UploadBytes > 0);
    }

    [Fact]
    public async Task C5_ClosedLoopbackPortIsUnreachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var client = Client(new SocketsHttpHandler { UseProxy = false }, url: $"http://127.0.0.1:{port}/inference");
        var watch = Stopwatch.StartNew();
        var result = await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", Limit, Ct);
        Assert.Equal(RemoteOutcome.Unreachable, result.Outcome);
        Assert.Null(result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), watch.Elapsed.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C6_DeadlineCoversHeadersAndBody(bool sendHeaders)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Serve()
        {
            using var socket = await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = socket.GetStream();
            var buffer = new byte[8192];
            using var request = new MemoryStream();
            var headerEnd = -1;
            var length = 0;
            do
            {
                var read = await stream.ReadAsync(buffer, stop.Token);
                if (read == 0) throw new IOException();
                request.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(request.ToArray());
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    var header = text[..headerEnd].Split("\r\n").Single(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    length = int.Parse(header.Split(':')[1].Trim());
                }
            } while (headerEnd < 0 || request.Length < headerEnd + 4 + length);
            if (sendHeaders)
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n"u8.ToArray(), stop.Token);
            accepted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        var serving = Serve();
        using var client = Client(new SocketsHttpHandler { UseProxy = false }, url: $"http://127.0.0.1:{port}/inference");
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", TimeSpan.FromMilliseconds(300), Ct);
            await accepted.Task.WaitAsync(Limit, Ct);
            Assert.Equal(RemoteOutcome.Timeout, result.Outcome);
            Assert.Equal(sendHeaders ? 200 : (int?)null, result.Status);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), watch.Elapsed.ToString());
        }
        finally
        {
            stop.Cancel();
            try { await serving; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{}")]
    [InlineData("{\"text\":5}")]
    [InlineData("{\"text\":\"\"}")]
    [InlineData("{\"text\":\"  \"}")]
    [InlineData("{\"text\":null}")]
    [InlineData("[]")]
    public async Task C7_MalformedSuccessIsFailed(string body)
    {
        using var client = Client(new Handler((_, _) => Task.FromResult(Response(body: body))));
        var result = await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", Limit, Ct);
        Assert.Equal(RemoteOutcome.Failed, result.Outcome);
        Assert.Equal(200, result.Status);
        Assert.Null(result.Text);
    }

    private sealed class StallingContent : HttpContent
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task C6_C8_DeadlineAndCancellationWithStub(bool sendHeaders, bool cancelCaller)
    {
        var time = new FakeTimeProvider();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new StallingContent();
        using var client = Client(new Handler(async (_, ct) =>
        {
            if (sendHeaders) return new(HttpStatusCode.OK) { Content = content };
            waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response();
        }), time: time);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var task = client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", TimeSpan.FromMilliseconds(300), cancel.Token);
        await (sendHeaders ? content.Waiting.Task : waiting.Task).WaitAsync(Limit, Ct);
        if (cancelCaller)
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        else
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
            var result = await task.WaitAsync(Limit, Ct);
            Assert.Equal(RemoteOutcome.Timeout, result.Outcome);
            Assert.Equal(sendHeaders ? 200 : (int?)null, result.Status);
            Assert.Equal(TimeSpan.FromMilliseconds(300), result.Elapsed);
        }
    }

    [Fact]
    public async Task C8_CallerCancellationPropagates()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(new Handler(async (_, ct) =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response();
        }));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var task = client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", Limit, cancel.Token);
        await waiting.Task.WaitAsync(Limit, Ct);
        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData("  http://h:1/x  ", "http://h:1/x")]
    [InlineData("https://h/x", "https://h/x")]
    [InlineData("", null)]
    [InlineData("h:1/x", null)]
    [InlineData("ftp://h/x", null)]
    [InlineData(null, null)]
    public void C9_AddressAndConfiguration(string? value, string? expected)
    {
        Assert.Equal(expected is not null, VoiceServerAddress.TryParse(value, out var uri));
        if (expected is not null) Assert.Equal(expected, uri.AbsoluteUri);
        var settings = new VoiceServerSettings { Url = value! };
        using var client = new RemoteWhisperClient(new Handler((_, _) => throw new InvalidOperationException()),
            () => settings, new Protector(), TimeProvider.System);
        Assert.Equal(expected is not null, client.IsConfigured);
        settings = new() { Url = "" };
        Assert.False(client.IsConfigured);
    }

    [Fact]
    public async Task ConnectionFailureReturnsUnreachableWithStub()
    {
        using var client = Client(new Handler((_, _) => throw new HttpRequestException("private data")));
        var result = await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", Limit, Ct);
        Assert.Equal(RemoteOutcome.Unreachable, result.Outcome);
        Assert.Null(result.Status);
        Assert.Null(result.Text);
        Assert.True(result.UploadBytes > 0);
    }

    [Fact]
    public async Task InvalidAudioAndUnexpectedErrorsReturnFailed()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("private data"));
        using var client = Client(handler);
        Assert.Equal(RemoteOutcome.Failed, (await client.TranscribeAsync(new([], TimeSpan.Zero, 0), "ru", "", Limit, Ct)).Outcome);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(RemoteOutcome.Failed, (await client.TranscribeAsync(OpusOggEncoderTests.Clip(), "ru", "", Limit, Ct)).Outcome);
        Assert.Equal(1, handler.Calls);
    }
}
