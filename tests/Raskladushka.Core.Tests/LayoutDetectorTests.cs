using Raskladushka.Core.Keys;
using Raskladushka.Core.Lang;
using Raskladushka.Core.Typing;

namespace Raskladushka.Core.Tests;

public class LayoutDetectorTests
{
    [Theory]
    [InlineData(Layout.En, "ghbdtn", Verdict.Switch)]
    [InlineData(Layout.En, "k.,k.", Verdict.Switch)]
    [InlineData(Layout.En, "[jhjij", Verdict.Switch)]
    [InlineData(Layout.En, ";bpym", Verdict.Switch)]
    [InlineData(Layout.Ru, "руддщ", Verdict.Switch)]
    [InlineData(Layout.Ru, "цщкдв", Verdict.Switch)]
    [InlineData(Layout.Ru, "пщщв", Verdict.Switch)]
    [InlineData(Layout.En, "hello", Verdict.Keep)]
    [InlineData(Layout.En, "hello.", Verdict.Keep)]
    [InlineData(Layout.En, "world", Verdict.Keep)]
    [InlineData(Layout.Ru, "привет", Verdict.Keep)]
    [InlineData(Layout.Ru, "это", Verdict.Keep)]
    [InlineData(Layout.Ru, "жизнь", Verdict.Keep)]
    [InlineData(Layout.Ru, "хорошо", Verdict.Keep)]
    [InlineData(Layout.Ru, "люблю", Verdict.Keep)]
    [InlineData(Layout.En, "don't", Verdict.Keep)]
    [InlineData(Layout.En, "doesn't", Verdict.Keep)]
    [InlineData(Layout.En, "it's", Verdict.Keep)]
    [InlineData(Layout.En, "can't", Verdict.Keep)]
    [InlineData(Layout.En, "we'll", Verdict.Keep)]
    [InlineData(Layout.En, "e.g.", Verdict.Keep)]
    [InlineData(Layout.En, "nginx", Verdict.Keep)]
    [InlineData(Layout.En, "kubectl", Verdict.Keep)]
    [InlineData(Layout.En, "ssh", Verdict.Keep)]
    [InlineData(Layout.En, "src", Verdict.Keep)]
    [InlineData(Layout.En, "tmp", Verdict.Keep)]
    [InlineData(Layout.En, "lf", Verdict.Keep)]
    [InlineData(Layout.Ru, "да", Verdict.Keep)]
    [InlineData(Layout.En, "Ghbdtn", Verdict.Switch)]
    [InlineData(Layout.En, "", Verdict.Keep)]
    [InlineData(Layout.En, "!!!", Verdict.Keep)]
    [InlineData(Layout.Ru, "фэи", Verdict.Keep)]
    [InlineData(Layout.En, "tot", Verdict.Switch)]
    [InlineData(Layout.En, "nen", Verdict.Switch)]
    [InlineData(Layout.En, ",tp", Verdict.Switch)]
    [InlineData(Layout.En, ",ele", Verdict.Switch)]
    [InlineData(Layout.En, "dc.", Verdict.Switch)]
    [InlineData(Layout.En, "it.", Verdict.Keep)]
    [InlineData(Layout.En, "here", Verdict.Keep)]
    [InlineData(Layout.En, "her", Verdict.Keep)]
    [InlineData(Layout.En, "the", Verdict.Keep)]
    [InlineData(Layout.En, "to", Verdict.Keep)]
    [InlineData(Layout.Ru, "руку", Verdict.Keep)]
    [InlineData(Layout.Ru, "рук", Verdict.Keep)]
    [InlineData(Layout.Ru, "еще", Verdict.Keep)]
    [InlineData(Layout.Ru, "тут", Verdict.Keep)]
    [InlineData(Layout.Ru, "шею", Verdict.Keep)]
    public void Rules(Layout current, string text, Verdict expected)
    {
        Assert.Equal(expected, CreateDetector().Decide(Strokes(text, current), current));
    }

    [Fact]
    public void ExceptionKeepsWord()
    {
        var detector = new LayoutDetector(LangData.Load(Layout.En), LangData.Load(Layout.Ru),
            word => word == "ghbdtn", () => new DetectorOptions());
        Assert.Equal(Verdict.Keep, detector.Decide(Strokes("ghbdtn", Layout.En), Layout.En));
    }

    [Fact]
    public void CapsPreservesSwitchDecision()
    {
        var strokes = Strokes("ghbdtn", Layout.En).Select(s => s with { Caps = true }).ToArray();
        Assert.Equal("GHBDTN", KeyMap.Render(strokes, Layout.En));
        Assert.Equal(Verdict.Switch, CreateDetector().Decide(strokes, Layout.En));
    }

    [Theory]
    [InlineData("«Привет!»", Layout.Ru, "привет")]
    [InlineData(".hello,", Layout.En, "hello")]
    [InlineData(".DON'T!", Layout.En, "don't")]
    [InlineData("éHelloя", Layout.En, "hello")]
    [InlineData("!Ёж!", Layout.Ru, "ёж")]
    [InlineData("!!!", Layout.Ru, "")]
    [InlineData("", Layout.En, "")]
    public void TrimUsesLayoutAlphabet(string text, Layout layout, string expected)
    {
        Assert.Equal(expected, LayoutDetector.TrimToLetters(text, layout));
    }

    [Fact]
    public void OptionsAreReadForEachDecision()
    {
        var options = new DetectorOptions();
        var detector = new LayoutDetector(LangData.Load(Layout.En), LangData.Load(Layout.Ru),
            _ => false, () => options);
        var strokes = Strokes("ghbdtn", Layout.En);
        Assert.Equal(Verdict.Switch, detector.Decide(strokes, Layout.En));
        options = options with { MinWordLength = 7 };
        Assert.Equal(Verdict.Keep, detector.Decide(strokes, Layout.En));
    }

    [Fact]
    public void YoSpellingRemainsAKnownLimitation()
    {
        KeyStroke[] strokes = [new(PhysKey.T), new(PhysKey.O), new(PhysKey.Backquote)];
        Assert.Equal("ещё", KeyMap.Render(strokes, Layout.Ru));
        Assert.Equal(Verdict.Keep, CreateDetector().Decide(strokes, Layout.En));
    }

    [Theory]
    [InlineData("ab", DecisionReason.TooShort, Verdict.Keep)]
    [InlineData("don't", DecisionReason.Contraction, Verdict.Keep)]
    [InlineData("ghbdtn", DecisionReason.AltMoreFrequent, Verdict.Switch)]
    [InlineData("here", DecisionReason.CurMoreFrequent, Verdict.Keep)]
    [InlineData("hello", DecisionReason.CurInDictionary, Verdict.Keep)]
    [InlineData("zzzzzzzz", DecisionReason.NoEvidence, Verdict.Keep)]
    public void ExplainNamesDecisionBranch(string text, DecisionReason reason, Verdict verdict)
    {
        var detector = CreateDetector();
        var word = Strokes(text, Layout.En);
        Assert.Equal(new Decision(verdict, reason), detector.Explain(word, Layout.En));
        Assert.Equal(verdict, detector.Decide(word, Layout.En));
    }

    [Fact]
    public void ExplainNamesException()
    {
        var detector = new LayoutDetector(LangData.Load(Layout.En), LangData.Load(Layout.Ru),
            word => word == "ghbdtn", () => new DetectorOptions());
        var word = Strokes("ghbdtn", Layout.En);
        Assert.Equal(new Decision(Verdict.Keep, DecisionReason.Exception), detector.Explain(word, Layout.En));
        Assert.Equal(Verdict.Keep, detector.Decide(word, Layout.En));
    }

    [Fact]
    public void ExplainNamesTrigramEvidence()
    {
        var detector = new LayoutDetector(LangModel.Build(Layout.En, [("hello", 100)]),
            LangModel.Build(Layout.Ru, [("ффф", 100000), ("яюэыьъ", 1)]), _ => false,
            () => new DetectorOptions { StrongPercentile = 0 });
        var word = Strokes("aaaa", Layout.En);
        Assert.Equal(new Decision(Verdict.Switch, DecisionReason.Trigram), detector.Explain(word, Layout.En));
        Assert.Equal(Verdict.Switch, detector.Decide(word, Layout.En));
    }

    private static LayoutDetector CreateDetector() => new(LangData.Load(Layout.En),
        LangData.Load(Layout.Ru), _ => false, () => new DetectorOptions());

    private static KeyStroke[] Strokes(string text, Layout layout) => text.Select(c =>
    {
        Assert.True(KeyMap.TryFromChar(c, layout, out var stroke));
        return stroke;
    }).ToArray();
}
