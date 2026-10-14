using Raskladushka.Core.Shell;

namespace Raskladushka.Core.Tests;

public class ClipboardPrivacyTests
{
    [Fact]
    public void FormatsHaveExpectedNamesAndData()
    {
        AssertFormats(ClipboardPrivacy.Formats);
    }

    [Fact]
    public void MutatingDataDoesNotAffectSubsequentCalls()
    {
        var formats = ClipboardPrivacy.Formats;
        foreach (var (_, data) in formats)
            Array.Fill(data, (byte)255);

        AssertFormats(ClipboardPrivacy.Formats);
    }

    private static void AssertFormats(IReadOnlyList<(string Format, byte[] Data)> formats)
    {
        Assert.Collection(formats,
            entry =>
            {
                Assert.Equal("ExcludeClipboardContentFromMonitorProcessing", entry.Format);
                Assert.Equal(new byte[] { 0 }, entry.Data);
            },
            entry =>
            {
                Assert.Equal("CanIncludeInClipboardHistory", entry.Format);
                Assert.Equal(new byte[] { 0, 0, 0, 0 }, entry.Data);
            },
            entry =>
            {
                Assert.Equal("CanUploadToCloudClipboard", entry.Format);
                Assert.Equal(new byte[] { 0, 0, 0, 0 }, entry.Data);
            });
    }
}
