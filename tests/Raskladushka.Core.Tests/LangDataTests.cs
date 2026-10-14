using System.Diagnostics;
using Raskladushka.Core.Keys;
using Raskladushka.Core.Lang;

namespace Raskladushka.Core.Tests;

public class LangDataTests
{
    [Fact]
    public void ParseNormalizesWordsAndSkipsMalformedLines()
    {
        using var reader = new StringReader("я 5081568\n\nbad\nПривет 12\n");
        Assert.Equal(new (string Word, long Count)[] { ("я", 5081568), ("привет", 12) },
            LangData.Parse(reader).ToArray());
    }

    [Fact]
    public void ParseSkipsInvalidCountsAndExtraFieldsWithoutThrowing()
    {
        using var reader = new StringReader(" \nmissing \nword nope\nword 1.5\nword 9223372036854775808\nword 1 extra\nHello 9223372036854775807\n");
        Assert.Equal(new (string Word, long Count)[] { ("hello", long.MaxValue) },
            LangData.Parse(reader).ToArray());
    }

    [Fact]
    public void EmbeddedModelsLoadQuicklyAndAreCachedAcrossThreads()
    {
        var timer = Stopwatch.StartNew();
        var ru = LangData.Load(Layout.Ru);
        var ruElapsed = timer.Elapsed;
        timer.Restart();
        var englishModels = new LangModel[16];
        Parallel.For(0, englishModels.Length, i => englishModels[i] = LangData.Load(Layout.En));
        var enElapsed = timer.Elapsed;
        var en = englishModels[0];
        Console.WriteLine($"First load: Ru={ruElapsed.TotalMilliseconds:F2} ms; En={enElapsed.TotalMilliseconds:F2} ms (16 concurrent calls)");
        Assert.True(ruElapsed < TimeSpan.FromSeconds(2));
        Assert.True(enElapsed < TimeSpan.FromSeconds(2));
        Assert.Same(ru, LangData.Load(Layout.Ru));
        Assert.All(englishModels, model => Assert.Same(en, model));
        Assert.Equal(Layout.Ru, ru.Lang);
        Assert.Equal(Layout.En, en.Lang);
        Assert.True(ru.WordCount > 40000);
        foreach (var word in new[] { "привет", "хорошо", "это" })
            Assert.True(ru.Contains(word));
        Assert.False(ru.Contains("руддщ"));
        Assert.True(en.Contains("hello"));
        Assert.True(en.Contains("world"));
        Assert.False(en.Contains("ghbdtn"));
        Assert.False(en.Contains("'s"));
        Assert.True(ru.Score("привет") > ru.Score("руддщ"));
        Assert.True(en.Score("hello") > en.Score("ghbdtn"));
    }
}
