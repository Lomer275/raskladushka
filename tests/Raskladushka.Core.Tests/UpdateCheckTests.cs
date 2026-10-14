using System.Text.Json;
using Raskladushka.Core.Updates;

namespace Raskladushka.Core.Tests;

public sealed class UpdateCheckTests : IDisposable
{
    private const string Page = UpdateCheck.PagePrefix + "tag/v0.3.0";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string StampPath => Path.Combine(_directory, "nested", "update-check.json");

    private static string Release(string tag = "v0.3.0", string page = Page) =>
        JsonSerializer.Serialize(new { tag_name = tag, html_url = page, draft = false, prerelease = false });

    [Fact]
    public void ParsesStableRelease()
    {
        var release = Assert.IsType<ReleaseInfo>(UpdateCheck.Parse(Release()));
        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal(Page, release.Page.AbsoluteUri);
    }

    [Theory]
    [InlineData("v0.3.0-rc.1")]
    [InlineData("v0.3")]
    [InlineData("0.3.0")]
    [InlineData("vX")]
    [InlineData("v0.3.0.1")]
    [InlineData("v 0.3.0")]
    [InlineData("v0.3.0 ")]
    [InlineData("v0.3.0\n")]
    [InlineData("v٠.3.0")]
    [InlineData("v1000000000.3.0")]
    public void RejectsInvalidTags(string tag) => Assert.Null(UpdateCheck.Parse(Release(tag)));

    [Theory]
    [InlineData("\"draft\":true,\"prerelease\":false")]
    [InlineData("\"draft\":false,\"prerelease\":true")]
    [InlineData("\"prerelease\":false")]
    [InlineData("\"draft\":\"false\",\"prerelease\":false")]
    [InlineData("\"draft\":false")]
    [InlineData("\"draft\":false,\"prerelease\":0")]
    public void RequiresExplicitFalseFlags(string flags) => Assert.Null(UpdateCheck.Parse(
        "{\"tag_name\":\"v0.3.0\",\"html_url\":\"" + Page + "\"," + flags + "}"));

    [Theory]
    [InlineData("https://evil.example/r")]
    [InlineData("http://github.com/Lomer275/raskladushka/releases/tag/v0.3.0")]
    [InlineData("https://github.com/Lomer275/raskladushka-evil/releases/tag/v0.3.0")]
    [InlineData("/Lomer275/raskladushka/releases/tag/v0.3.0")]
    [InlineData("https://github.com/Lomer275/raskladushka/releases/../../other")]
    public void RejectsUntrustedPages(string page) => Assert.Null(UpdateCheck.Parse(Release(page: page)));

    [Theory]
    [InlineData("не json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"draft\":false,\"prerelease\":false,\"tag_name\":1}")]
    public void InvalidJsonDoesNotThrow(string json) => Assert.Null(UpdateCheck.Parse(json));

    [Theory]
    [InlineData("0.3.0", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.2.0", "0.3.0", false)]
    [InlineData("0.2.0", "0.2.0.0", false)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.2.0.1", "0.2.0", false)]
    public void ComparesOnlyThreeComponents(string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(new(new Version(latest), new Uri(Page)), new Version(current)));

    [Theory]
    [InlineData(null, true)]
    [InlineData(-144, false)]
    [InlineData(-168, true)]
    [InlineData(1, true)]
    public void ChecksWeeklyIntervalAndFutureStamp(int? hours, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, UpdateCheck.IsDue(hours is { } h ? now.AddHours(h) : null, now));
    }

    [Theory]
    [InlineData("0.2.0+abc", "0.2.0")]
    [InlineData("0.2.0-rc.1+abc", "0.2.0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.2", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void ParsesCurrentVersion(string input, string? expected) =>
        Assert.Equal(expected is null ? null : new Version(expected), UpdateCheck.ParseCurrent(input));

    [Fact]
    public void ReadsMissingAndMalformedStampAsNull()
    {
        Assert.Null(UpdateStamp.Read(StampPath));
        Directory.CreateDirectory(Path.GetDirectoryName(StampPath)!);
        foreach (var invalid in new[] { "мусор", "{}", "null", "[]", "{\"lastCheck\":123}", "{\"lastCheck\":\"bad\"}" })
        {
            File.WriteAllText(StampPath, invalid);
            Assert.Null(UpdateStamp.Read(StampPath));
        }
        Assert.Null(UpdateStamp.Read(_directory));
    }

    [Fact]
    public void WritesStampCreatingDirectoryAndReplacingPrevious()
    {
        var value = new DateTimeOffset(2026, 10, 14, 12, 34, 56, TimeSpan.FromHours(3));
        UpdateStamp.Write(StampPath, value);
        Assert.Equal(value, UpdateStamp.Read(StampPath));
        UpdateStamp.Write(StampPath, value.AddDays(7));
        Assert.Equal(value.AddDays(7), UpdateStamp.Read(StampPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StampPath)!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
