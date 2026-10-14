using Raskladushka.Core.Keys;
using Raskladushka.Core.Typing;

namespace Raskladushka.Core.Tests;

public class WordBufferTests
{
    [Fact]
    public void WordKeysAccumulateWithoutEndingWord()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G, PhysKey.H, PhysKey.B);
        AssertTarget(buffer, [new(PhysKey.G), new(PhysKey.H), new(PhysKey.B)], []);
    }

    [Fact]
    public void SpaceEndsWordAndStartsTrailing()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G, PhysKey.H);
        AssertEnd(buffer.OnChar(new(PhysKey.Space)), [new(PhysKey.G), new(PhysKey.H)], new(PhysKey.Space));
        AssertTarget(buffer, [new(PhysKey.G), new(PhysKey.H)], [new(PhysKey.Space)]);
    }

    [Fact]
    public void AdditionalSpaceExtendsTrailingWithoutEndingWord()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G);
        buffer.OnChar(new(PhysKey.Space));
        Assert.Null(buffer.OnChar(new(PhysKey.Space)));
        AssertTarget(buffer, [new(PhysKey.G)], [new(PhysKey.Space), new(PhysKey.Space)]);
    }

    [Fact]
    public void RemovingTerminatorRestoresCurrentWord()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G, PhysKey.H);
        buffer.OnChar(new(PhysKey.Space));
        buffer.OnBackspace();
        AssertTarget(buffer, [new(PhysKey.G), new(PhysKey.H)], []);
        TypeWord(buffer, PhysKey.B);
        AssertEnd(buffer.OnChar(new(PhysKey.Space)),
            [new(PhysKey.G), new(PhysKey.H), new(PhysKey.B)], new(PhysKey.Space));
    }

    [Fact]
    public void BackspaceRemovesCurrentKeysAndDoesNothingWhenEmpty()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G, PhysKey.H);
        buffer.OnBackspace();
        AssertTarget(buffer, [new(PhysKey.G)], []);
        buffer.OnBackspace();
        Assert.Null(buffer.PeekFixTarget());
        buffer.OnBackspace();
        Assert.Null(buffer.PeekFixTarget());
    }

    [Fact]
    public void StartingNewWordForgetsPreviousWord()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G);
        buffer.OnChar(new(PhysKey.Space));
        TypeWord(buffer, PhysKey.X);
        AssertTarget(buffer, [new(PhysKey.X)], []);
        buffer.OnBackspace();
        Assert.Null(buffer.PeekFixTarget());
    }

    [Fact]
    public void SpaceOnEmptyBufferDoesNothing()
    {
        var buffer = new WordBuffer();
        Assert.Null(buffer.OnChar(new(PhysKey.Space)));
        Assert.Null(buffer.PeekFixTarget());
    }

    [Fact]
    public void ResetClearsCurrentWord()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G, PhysKey.H);
        buffer.Reset();
        Assert.Null(buffer.PeekFixTarget());
        Assert.Null(buffer.OnChar(new(PhysKey.Space)));
    }

    [Fact]
    public void DigitEndsWord()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G, PhysKey.H);
        AssertEnd(buffer.OnChar(new(PhysKey.D1)), [new(PhysKey.G), new(PhysKey.H)], new(PhysKey.D1));
    }

    [Fact]
    public void PeriodBelongsToWord()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.H, PhysKey.E, PhysKey.L, PhysKey.L, PhysKey.O, PhysKey.Period);
        AssertEnd(buffer.OnChar(new(PhysKey.Space)),
            [new(PhysKey.H), new(PhysKey.E), new(PhysKey.L), new(PhysKey.L), new(PhysKey.O), new(PhysKey.Period)],
            new(PhysKey.Space));
    }

    [Fact]
    public void OverlongWordIsIgnoredUntilTerminator()
    {
        var buffer = new WordBuffer();
        for (var i = 0; i < 70; i++)
            Assert.Null(buffer.OnChar(new(PhysKey.A)));
        Assert.Null(buffer.PeekFixTarget());
        Assert.Null(buffer.OnChar(new(PhysKey.Space)));
        Assert.Null(buffer.PeekFixTarget());
        TypeWord(buffer, PhysKey.G, PhysKey.H);
        AssertEnd(buffer.OnChar(new(PhysKey.Space)), [new(PhysKey.G), new(PhysKey.H)], new(PhysKey.Space));
    }

    [Fact]
    public void Exactly64KeysAreAcceptedAnd65thClearsBuffer()
    {
        Assert.Equal(64, WordBuffer.MaxWordLength);
        var buffer = new WordBuffer();
        var word = Enumerable.Repeat(new KeyStroke(PhysKey.A), 64).ToArray();
        foreach (var stroke in word)
            Assert.Null(buffer.OnChar(stroke));
        AssertTarget(buffer, word, []);
        AssertEnd(buffer.OnChar(new(PhysKey.Space)), word, new(PhysKey.Space));
        foreach (var stroke in word)
            Assert.Null(buffer.OnChar(stroke));
        Assert.Null(buffer.OnChar(new(PhysKey.A)));
        Assert.Null(buffer.PeekFixTarget());
        buffer.OnBackspace();
        Assert.Null(buffer.OnChar(new(PhysKey.G)));
        Assert.Null(buffer.PeekFixTarget());
        Assert.Null(buffer.OnChar(new(PhysKey.D1)));
        TypeWord(buffer, PhysKey.H);
        AssertTarget(buffer, [new(PhysKey.H)], []);
    }

    [Fact]
    public void BackspaceRemovesTrailingKeysBeforeWordKeys()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G, PhysKey.H);
        buffer.OnChar(new(PhysKey.Space));
        buffer.OnChar(new(PhysKey.D1));
        buffer.OnBackspace();
        AssertTarget(buffer, [new(PhysKey.G), new(PhysKey.H)], [new(PhysKey.Space)]);
        buffer.OnBackspace();
        AssertTarget(buffer, [new(PhysKey.G), new(PhysKey.H)], []);
        buffer.OnBackspace();
        AssertTarget(buffer, [new(PhysKey.G)], []);
    }

    [Fact]
    public void ResetClearsPreviousWordAndOverflowSuppression()
    {
        var buffer = new WordBuffer();
        TypeWord(buffer, PhysKey.G);
        buffer.OnChar(new(PhysKey.Space));
        buffer.Reset();
        Assert.Null(buffer.PeekFixTarget());
        Assert.Null(buffer.OnChar(new(PhysKey.Space)));
        for (var i = 0; i < 65; i++)
            buffer.OnChar(new(PhysKey.A));
        buffer.Reset();
        TypeWord(buffer, PhysKey.H);
        AssertTarget(buffer, [new(PhysKey.H)], []);
    }

    [Fact]
    public void ReturnedListsAreSnapshotsAndPreserveModifiers()
    {
        var buffer = new WordBuffer();
        KeyStroke[] word = [new(PhysKey.G, Shift: true), new(PhysKey.H, Caps: true), new(PhysKey.Period, true, true)];
        var terminator = new KeyStroke(PhysKey.Slash, true, true);
        foreach (var stroke in word)
            Assert.Null(buffer.OnChar(stroke));
        var current = Assert.IsType<FixTarget>(buffer.PeekFixTarget());
        var end = Assert.IsType<WordEnd>(buffer.OnChar(terminator));
        var previous = Assert.IsType<FixTarget>(buffer.PeekFixTarget());
        buffer.OnChar(new(PhysKey.Space));
        buffer.OnBackspace();
        buffer.OnBackspace();
        buffer.OnBackspace();
        buffer.OnChar(new(PhysKey.B));
        buffer.Reset();
        Assert.Equal(word, current.Word);
        Assert.Empty(current.Trailing);
        Assert.Equal(word, end.Word);
        Assert.Equal(terminator, end.Terminator);
        Assert.Equal(word, previous.Word);
        Assert.Equal(new[] { terminator }, previous.Trailing);
    }

    private static void TypeWord(WordBuffer buffer, params PhysKey[] keys)
    {
        foreach (var key in keys)
            Assert.Null(buffer.OnChar(new(key)));
    }

    private static void AssertTarget(WordBuffer buffer, KeyStroke[] word, KeyStroke[] trailing)
    {
        var target = Assert.IsType<FixTarget>(buffer.PeekFixTarget());
        Assert.Equal(word, target.Word);
        Assert.Equal(trailing, target.Trailing);
    }

    private static void AssertEnd(WordEnd? result, KeyStroke[] word, KeyStroke terminator)
    {
        var end = Assert.IsType<WordEnd>(result);
        Assert.Equal(word, end.Word);
        Assert.Equal(terminator, end.Terminator);
    }
}
