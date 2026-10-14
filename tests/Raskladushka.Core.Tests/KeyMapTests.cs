using Raskladushka.Core.Keys;

namespace Raskladushka.Core.Tests;

public class KeyMapTests
{
    [Theory]
    [InlineData("ghbdtn vbh", Layout.En)]
    [InlineData("привет мир", Layout.Ru)]
    [InlineData("abc абвг", Layout.Ru)]
    [InlineData("", Layout.En)]
    [InlineData("123 !?", Layout.En)]
    [InlineData("abc абв", Layout.En)]
    [InlineData("Ёё ABC", Layout.En)]
    [InlineData("ЁёЯ A!?123", Layout.Ru)]
    public void GuessLayoutCountsLetters(string text, Layout expected)
    {
        Assert.Equal(expected, KeyMap.GuessLayout(text));
    }

    [Theory]
    [InlineData(Layout.Ru, "привет")]
    [InlineData(Layout.En, "ghbdtn")]
    public void RenderGreeting(Layout layout, string expected)
    {
        Assert.Equal(expected, KeyMap.Render([
            new(PhysKey.G), new(PhysKey.H), new(PhysKey.B),
            new(PhysKey.D), new(PhysKey.T), new(PhysKey.N)], layout));
    }

    [Fact]
    public void RenderRussianLettersOnPunctuationKeys()
    {
        Assert.Equal("люблю", KeyMap.Render([
            new(PhysKey.K), new(PhysKey.Period), new(PhysKey.Comma),
            new(PhysKey.K), new(PhysKey.Period)], Layout.Ru));
        Assert.Equal("хорошо", KeyMap.Render([
            new(PhysKey.LBracket), new(PhysKey.J), new(PhysKey.H),
            new(PhysKey.J), new(PhysKey.I), new(PhysKey.J)], Layout.Ru));
    }

    [Theory]
    [InlineData(true, false, 'П')]
    [InlineData(false, true, 'П')]
    [InlineData(true, true, 'п')]
    public void LetterCaseUsesShiftXorCaps(bool shift, bool caps, char expected)
    {
        Assert.Equal(expected, KeyMap.ToChar(new(PhysKey.G, shift, caps), Layout.Ru));
    }

    [Theory]
    [InlineData(Layout.En, '.')]
    [InlineData(Layout.Ru, 'Ю')]
    public void CapsDependsOnLayout(Layout layout, char expected)
    {
        Assert.Equal(expected, KeyMap.ToChar(new(PhysKey.Period, Caps: true), layout));
    }

    [Theory]
    [InlineData(PhysKey.Slash, Layout.En, '/', '?')]
    [InlineData(PhysKey.Slash, Layout.Ru, '.', ',')]
    [InlineData(PhysKey.Backslash, Layout.En, '\\', '|')]
    [InlineData(PhysKey.Backslash, Layout.Ru, '\\', '/')]
    [InlineData(PhysKey.Backquote, Layout.En, '`', '~')]
    [InlineData(PhysKey.Backquote, Layout.Ru, 'ё', 'Ё')]
    public void PunctuationPairs(PhysKey key, Layout layout, char normal, char shifted)
    {
        Assert.Equal(normal, KeyMap.ToChar(new(key), layout));
        Assert.Equal(shifted, KeyMap.ToChar(new(key, Shift: true), layout));
    }

    [Theory]
    [InlineData(Layout.En, "@#$^&")]
    [InlineData(Layout.Ru, "\"№;:?")]
    public void ShiftedDigits(Layout layout, string expected)
    {
        Assert.Equal(expected, KeyMap.Render([
            new(PhysKey.D2, true), new(PhysKey.D3, true), new(PhysKey.D4, true),
            new(PhysKey.D6, true), new(PhysKey.D7, true)], layout));
    }

    [Fact]
    public void WordKeysAreExactlyLettersInEitherLayout()
    {
        PhysKey[] expected = [
            PhysKey.A, PhysKey.B, PhysKey.C, PhysKey.D, PhysKey.E, PhysKey.F,
            PhysKey.G, PhysKey.H, PhysKey.I, PhysKey.J, PhysKey.K, PhysKey.L,
            PhysKey.M, PhysKey.N, PhysKey.O, PhysKey.P, PhysKey.Q, PhysKey.R,
            PhysKey.S, PhysKey.T, PhysKey.U, PhysKey.V, PhysKey.W, PhysKey.X,
            PhysKey.Y, PhysKey.Z, PhysKey.Backquote, PhysKey.LBracket,
            PhysKey.RBracket, PhysKey.Semicolon, PhysKey.Quote, PhysKey.Comma,
            PhysKey.Period];
        Assert.Equal(33, Enum.GetValues<PhysKey>().Count(KeyMap.IsWordKey));
        foreach (var key in Enum.GetValues<PhysKey>())
            Assert.Equal(expected.Contains(key), KeyMap.IsWordKey(key));
        Assert.False(KeyMap.IsLetter(PhysKey.Period, Layout.En));
        Assert.True(KeyMap.IsLetter(PhysKey.Period, Layout.Ru));
    }

    [Theory]
    [InlineData("ghbdtn", Layout.En, Layout.Ru, "привет")]
    [InlineData("Привет, мир", Layout.Ru, Layout.En, "Ghbdtn? vbh")]
    [InlineData("руддщ", Layout.Ru, Layout.En, "hello")]
    [InlineData("a→b é", Layout.En, Layout.Ru, "ф→и é")]
    public void ConvertPreservesPhysicalKeysAndUnknownCharacters(
        string text, Layout from, Layout to, string expected)
    {
        Assert.Equal(expected, KeyMap.Convert(text, from, to));
    }

    public static IEnumerable<object[]> AllStrokes()
    {
        foreach (var layout in Enum.GetValues<Layout>())
        foreach (var key in Enum.GetValues<PhysKey>())
        foreach (var shift in new[] { false, true })
        foreach (var caps in new[] { false, true })
            yield return [key, shift, caps, layout];
    }

    [Theory]
    [MemberData(nameof(AllStrokes))]
    public void EveryStrokeRendersAndRoundTrips(PhysKey key, bool shift, bool caps, Layout layout)
    {
        var stroke = new KeyStroke(key, shift, caps);
        var character = KeyMap.ToChar(stroke, layout);
        Assert.Equal(character.ToString(), KeyMap.Render([stroke], layout));
        Assert.True(KeyMap.TryFromChar(character, layout, out var restored));
        Assert.False(restored.Caps);
        Assert.Equal(character, KeyMap.ToChar(restored, layout));
        if (char.IsUpper(character))
            Assert.True(restored.Shift);
    }

    [Fact]
    public void UnknownCharactersHaveNoKey()
    {
        foreach (var layout in Enum.GetValues<Layout>())
        {
            Assert.False(KeyMap.TryFromChar('→', layout, out _));
            Assert.False(KeyMap.TryFromChar('é', layout, out _));
        }
    }

    [Fact]
    public void OtherSwapsLayouts()
    {
        Assert.Equal(Layout.Ru, KeyMap.Other(Layout.En));
        Assert.Equal(Layout.En, KeyMap.Other(Layout.Ru));
    }
}
