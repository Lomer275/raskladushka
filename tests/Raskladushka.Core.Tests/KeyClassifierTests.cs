using Raskladushka.Core.Keys;

namespace Raskladushka.Core.Tests;

public class KeyClassifierTests
{
    [Theory]
    [InlineData(0x41, 0x10, PhysKey.Q)]
    [InlineData(0xBF, 0x35, PhysKey.Slash)]
    [InlineData(0x20, 0x39, PhysKey.Space)]
    [InlineData(0xC0, 0x29, PhysKey.Backquote)]
    public void CharacterUsesScanCode(int vk, int scan, PhysKey expected)
    {
        Assert.Equal(new KeyClass(KeyKind.Char, expected), KeyClassifier.Classify(vk, scan, false));
    }

    [Theory]
    [InlineData(0x08, 0x0E, false, KeyKind.Backspace)]
    [InlineData(0x10, 0x2A, false, KeyKind.Shift)]
    [InlineData(0xA0, 0x2A, false, KeyKind.Shift)]
    [InlineData(0xA1, 0x36, false, KeyKind.Shift)]
    [InlineData(0x14, 0x3A, false, KeyKind.CapsLock)]
    [InlineData(0x11, 0x1D, false, KeyKind.Modifier)]
    [InlineData(0xA2, 0x1D, false, KeyKind.Modifier)]
    [InlineData(0xA3, 0x1D, true, KeyKind.Modifier)]
    [InlineData(0x12, 0x38, false, KeyKind.Modifier)]
    [InlineData(0xA4, 0x38, false, KeyKind.Modifier)]
    [InlineData(0xA5, 0x38, true, KeyKind.Modifier)]
    [InlineData(0x5B, 0x5B, true, KeyKind.Modifier)]
    [InlineData(0x5C, 0x5C, true, KeyKind.Modifier)]
    [InlineData(0x0D, 0x1C, false, KeyKind.Reset)]
    [InlineData(0x09, 0x0F, false, KeyKind.Reset)]
    [InlineData(0x1B, 0x01, false, KeyKind.Reset)]
    [InlineData(0x25, 0x4B, true, KeyKind.Reset)]
    [InlineData(0x6F, 0x35, true, KeyKind.Reset)]
    [InlineData(0xBF, 0x35, true, KeyKind.Reset)]
    [InlineData(0x13, 0x45, false, KeyKind.Ignore)]
    [InlineData(0x91, 0x46, false, KeyKind.Ignore)]
    [InlineData(0x70, 0x3B, false, KeyKind.Ignore)]
    [InlineData(0x87, 0x76, false, KeyKind.Ignore)]
    [InlineData(0, 0, false, KeyKind.Ignore)]
    [InlineData(0x41, 0x1E, true, KeyKind.Ignore)]
    public void ClassifiesSpecialKeys(int vk, int scan, bool extended, KeyKind expected)
    {
        Assert.Equal(expected, KeyClassifier.Classify(vk, scan, extended).Kind);
    }

    [Fact]
    public void NavigationAndNumpadReset()
    {
        foreach (var vk in Enumerable.Range(0x21, 8).Concat([0x2D, 0x2E])
                     .Concat(Enumerable.Range(0x60, 16)))
            Assert.Equal(KeyKind.Reset, KeyClassifier.Classify(vk, 0, false).Kind);
    }

    [Fact]
    public void AllFunctionKeysAreIgnored()
    {
        foreach (var vk in Enumerable.Range(0x70, 24))
            Assert.Equal(KeyKind.Ignore, KeyClassifier.Classify(vk, 0, false).Kind);
    }
}
