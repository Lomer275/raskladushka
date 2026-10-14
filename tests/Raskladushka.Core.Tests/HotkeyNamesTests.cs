using Raskladushka.Core.Config;

namespace Raskladushka.Core.Tests;

public sealed class HotkeyNamesTests
{
    [Theory]
    [InlineData("rcontrol", 0xA3)]
    [InlineData("RAlt", 0xA5)]
    [InlineData("RShift", 0xA1)]
    [InlineData("Pause", 0x13)]
    [InlineData("ScrollLock", 0x91)]
    public void RecognizesNamedKeys(string name, int expected)
    {
        Assert.True(HotkeyNames.TryGetVk(name, out var vk));
        Assert.Equal(expected, vk);
    }

    [Fact]
    public void RecognizesFunctionKeys()
    {
        for (var number = 13; number <= 24; number++)
        {
            Assert.True(HotkeyNames.TryGetVk($"f{number}", out var vk));
            Assert.Equal(0x7C + number - 13, vk);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("F12")]
    [InlineData("F25")]
    [InlineData("F013")]
    public void RejectsUnsupportedKeys(string? name) => Assert.False(HotkeyNames.TryGetVk(name!, out _));
}
