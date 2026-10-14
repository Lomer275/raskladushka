using System.Text.Json;

namespace Raskladushka.App.SelfTest;

public sealed record SelfTestCase(string Name, string Expected, string Actual, bool Passed, string? Error = null);

public sealed class SelfTestReport
{
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public List<SelfTestCase> Cases { get; } = [];
    public List<string> CleanupErrors { get; } = [];
    public Dictionary<string, object> Diagnostics { get; } = [];
    public int Total => Cases.Count;
    public int Passed => Cases.Count(result => result.Passed);
    public int Failed => Total - Passed;
    public bool Success => Total > 0 && Failed == 0 && CleanupErrors.Count == 0;

    public string Save()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raskladushka", "logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"selftest-{StartedAt:yyyyMMdd-HHmmss-fff}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
