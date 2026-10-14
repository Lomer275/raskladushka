using System.Net.Http;

namespace Raskladushka.App.Shell;

public static class VoiceHttp
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Raskladushka/{Application.ProductVersion.Split('+')[0]}");
        return client;
    }
}
