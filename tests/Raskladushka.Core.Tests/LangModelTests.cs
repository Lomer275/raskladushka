using Raskladushka.Core.Keys;
using Raskladushka.Core.Lang;

namespace Raskladushka.Core.Tests;

public class LangModelTests
{
    private static LangModel RussianModel() =>
        LangModel.Build(Layout.Ru, [("привет", 100), ("пример", 50), ("мир", 10)]);

    [Fact]
    public void FrequenciesAreNormalizedCounts()
    {
        var model = RussianModel();
        Assert.Equal(100.0 / 160, model.Frequency("привет"), 12);
        Assert.Equal(50.0 / 160, model.Frequency("пример"), 12);
        Assert.Equal(10.0 / 160, model.Frequency("мир"), 12);
        Assert.Equal(1, model.Words.Sum(entry => entry.Frequency), 12);
        Assert.Equal(model.WordCount, model.Words.Count());
        Assert.All(model.Words, entry => Assert.Equal(model.Frequency(entry.Word), entry.Frequency));
        Assert.Equal(0, model.Frequency("нет"));
        var combined = LangModel.Build(Layout.En, [("Hello", 5), ("hello", 7), ("world", 20), ("it's", 900)]);
        Assert.Equal(12.0 / 32, combined.Frequency("hello"), 12);
    }

    [Fact]
    public void CommonRussianWordOutweighsDictionaryTail()
    {
        var model = LangData.Load(Layout.Ru);
        var tail = model.Words.OrderBy(entry => entry.Frequency).Take(1000).ToArray();
        Assert.All(tail, entry => Assert.True(model.Frequency("еще") > 100 * entry.Frequency));
    }

    [Fact]
    public void ContainsMatchesWholeLowercaseWords()
    {
        var model = RussianModel();
        Assert.Equal(Layout.Ru, model.Lang);
        Assert.Equal(3, model.WordCount);
        Assert.True(model.Contains("привет"));
        Assert.False(model.Contains("приве"));
        Assert.False(model.Contains("Привет"));
    }

    [Fact]
    public void ScorePrefersFamiliarTrigramsAndRejectsInvalidWords()
    {
        var model = RussianModel();
        Assert.True(model.Score("привет") > model.Score("пртвие"));
        foreach (var word in new[] { "hello", "", "Привет", "мир!", "м1р" })
            Assert.Equal(double.NegativeInfinity, model.Score(word));
    }

    [Fact]
    public void PercentilesUseNearestRankIncludingEndpoints()
    {
        var model = RussianModel();
        var scores = new[] { "привет", "пример", "мир" }.Select(model.Score).Order().ToArray();
        Assert.Equal(scores[0], model.Percentile(0));
        Assert.Equal(scores[0], model.Percentile(1));
        Assert.Equal(scores[1], model.Percentile(50));
        Assert.Equal(scores[2], model.Percentile(100));
    }

    [Fact]
    public void BuildNormalizesFiltersAndSumsCountsBeforeWeighting()
    {
        var model = LangModel.Build(Layout.En,
            [("Hello", 5), ("hello", 7), ("it's", 9), ("x1", 3), ("", 1), ("мир", 2), ("world", 20)]);
        var combined = LangModel.Build(Layout.En, [("hello", 12), ("world", 20)]);
        Assert.Equal(2, model.WordCount);
        Assert.True(model.Contains("hello"));
        Assert.Equal(combined.Score("hello"), model.Score("hello"));
        var single = LangModel.Build(Layout.En, [("Hello", 5), ("hello", 7), ("it's", 9), ("x1", 3)]);
        Assert.Equal(1, single.WordCount);
        Assert.True(single.Contains("hello"));
    }

    [Fact]
    public void ScoreUsesWeightedConditionalProbabilitiesAndBothBoundaries()
    {
        var model = LangModel.Build(Layout.En, [("a", 90), ("aa", 990)]);
        var start = Math.Log10(5.1 / 7.7);
        Assert.Equal((start + Math.Log10(2.1 / 7.7)) / 2, model.Score("a"), 12);
        Assert.Equal((start + Math.Log10(3.1 / 7.7) + Math.Log10(3.1 / 5.7)) / 3,
            model.Score("aa"), 12);
        Assert.Equal((Math.Log10(0.1 / 7.7) + Math.Log10(1.0 / 27)) / 2,
            model.Score("b"), 12);
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz", Layout.En, true)]
    [InlineData("абвгдеёжзийклмнопрстуфхцчшщъыьэюя", Layout.Ru, true)]
    [InlineData("ё", Layout.En, false)]
    [InlineData("Ё", Layout.Ru, false)]
    [InlineData("abc", Layout.Ru, false)]
    [InlineData("é", Layout.En, false)]
    [InlineData("", Layout.En, false)]
    [InlineData("a b", Layout.En, false)]
    public void AlphabetIsExact(string word, Layout lang, bool expected)
    {
        Assert.Equal(expected, LangModel.IsAlphabetic(word, lang));
    }

    [Fact]
    public void EmptyModelHasNoPercentiles()
    {
        var model = LangModel.Build(Layout.Ru, []);
        Assert.Equal(0, model.WordCount);
        Assert.False(model.Contains("я"));
        foreach (var p in new[] { 0, 50, 100 })
            Assert.Equal(double.NegativeInfinity, model.Percentile(p));
        Assert.Equal(Math.Log10(1.0 / 34), model.Score("ё"), 12);
    }
}
