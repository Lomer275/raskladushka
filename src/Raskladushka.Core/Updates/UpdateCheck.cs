using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Raskladushka.Core.Updates;

public sealed record ReleaseInfo(Version Version, Uri Page);

public static class UpdateCheck
{
    public const string RepoUrl = "https://github.com/Lomer275/raskladushka";
    public const string LatestUrl = "https://api.github.com/repos/Lomer275/raskladushka/releases/latest";
    public const string PagePrefix = "https://github.com/Lomer275/raskladushka/releases/";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("draft").ValueKind != JsonValueKind.False ||
                root.GetProperty("prerelease").ValueKind != JsonValueKind.False) return null;
            var tag = root.GetProperty("tag_name").GetString();
            if (tag is null || !tag.StartsWith('v')) return null;
            var version = ParseTriple(tag[1..]);
            if (version is null || !Uri.TryCreate(root.GetProperty("html_url").GetString(), UriKind.Absolute, out var page) ||
                page.Scheme != Uri.UriSchemeHttps || !page.AbsoluteUri.StartsWith(PagePrefix, StringComparison.Ordinal)) return null;
            return new(version, page);
        }
        catch (Exception) { return null; }
    }

    public static Version? ParseCurrent(string productVersion)
    {
        if (productVersion is null) return null;
        var suffix = productVersion.IndexOfAny(['-', '+']);
        return ParseTriple(suffix < 0 ? productVersion : productVersion[..suffix]);
    }

    private static Version? ParseTriple(string value)
    {
        if (!Regex.IsMatch(value, @"\A[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\z", RegexOptions.CultureInvariant)) return null;
        var parts = value.Split('.');
        return new Version(int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    public static bool IsNewer(ReleaseInfo release, Version current) =>
        (release.Version.Major, release.Version.Minor, release.Version.Build)
            .CompareTo((current.Major, current.Minor, current.Build)) > 0;

    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is null || lastCheck > now || now - lastCheck.Value >= Interval;
}

public static class UpdateStamp
{
    public static DateTimeOffset? Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("lastCheck").GetDateTimeOffset();
        }
        catch (Exception) { return null; }
    }

    public static void Write(string path, DateTimeOffset value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { lastCheck = value.ToString("O", CultureInfo.InvariantCulture) }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
