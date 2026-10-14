namespace Raskladushka.Core.Shell;

public static class ClipboardPrivacy
{
    // Formats Windows reads to keep clipboard content out of Win+V history and cloud sync.
    public static IReadOnlyList<(string Format, byte[] Data)> Formats =>
    [
        ("ExcludeClipboardContentFromMonitorProcessing", [0]),
        ("CanIncludeInClipboardHistory", [0, 0, 0, 0]),
        ("CanUploadToCloudClipboard", [0, 0, 0, 0])
    ];
}
