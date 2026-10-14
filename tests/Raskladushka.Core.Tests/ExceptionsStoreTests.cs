using Raskladushka.Core.Config;

namespace Raskladushka.Core.Tests;

public sealed class ExceptionsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "nested", "exceptions.txt");

    [Fact]
    public void AddsImmediatelyAndPersistsWithoutDuplicates()
    {
        using (var store = new ExceptionsStore(StorePath))
        {
            store.Add(" GHBDTN ");
            Assert.True(store.Contains("ghbdtn"));
            store.Add("ghbdtn");
            store.Add("");
            store.Add("   ");
            store.Flush();
            Assert.Equal(new[] { "ghbdtn" }, File.ReadAllLines(StorePath));
        }
        using var reloaded = new ExceptionsStore(StorePath);
        Assert.True(reloaded.Contains("ghbdtn"));
    }

    [Fact]
    public void RemovesAndDisposeFlushes()
    {
        using (var store = new ExceptionsStore(StorePath))
        {
            store.Add("ghbdtn");
            store.Add("keep");
            Assert.True(store.Remove("ghbdtn"));
            Assert.False(store.Remove("ghbdtn"));
            store.Flush();
        }
        using var reloaded = new ExceptionsStore(StorePath);
        Assert.False(reloaded.Contains("ghbdtn"));
        Assert.True(reloaded.Contains("keep"));
        using (var store = new ExceptionsStore(StorePath)) store.Add("disposed");
        using var afterDispose = new ExceptionsStore(StorePath);
        Assert.True(afterDispose.Contains("disposed"));
    }

    [Fact]
    public void IgnoresBlankLinesAndComments()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "\n  \n# comment\n  # another\n WORD \n");
        using var store = new ExceptionsStore(StorePath);
        Assert.True(store.Contains("word"));
        Assert.False(store.Contains(""));
        Assert.False(store.Contains("# comment"));
    }

    [Fact]
    public void DiskFailuresKeepMemoryUsable()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file");
        File.WriteAllText(blocker, "");
        using var store = new ExceptionsStore(Path.Combine(blocker, "exceptions.txt"));
        store.Add("word");
        store.Flush();
        Assert.True(store.Contains("word"));
        Assert.True(store.Remove("word"));
        using var unreadable = new ExceptionsStore(_directory);
        unreadable.Add("memory");
        unreadable.Flush();
        Assert.True(unreadable.Contains("memory"));
    }

    [Fact]
    public void ConcurrentChangesPersistFinalState()
    {
        using var store = new ExceptionsStore(StorePath);
        Parallel.For(0, 200, index =>
        {
            var word = $"word{index}";
            store.Add(word);
            Assert.True(store.Contains(word));
            if (index % 2 == 0) Assert.True(store.Remove(word));
        });
        store.Flush();
        using var reloaded = new ExceptionsStore(StorePath);
        for (var index = 0; index < 200; index++) Assert.Equal(index % 2 != 0, reloaded.Contains($"word{index}"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
