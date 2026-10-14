using Raskladushka.Core.Voice;

namespace Raskladushka.Core.Tests;

public class VoiceVariantChooserTests
{
    [Theory]
    [InlineData("NVIDIA", true, VoiceVariant.Gpu)]
    [InlineData("NVIDIA", false, VoiceVariant.Cpu)]
    [InlineData("Advanced Micro Devices, Inc.", true, VoiceVariant.Cpu)]
    [InlineData("Intel Corporation|NVIDIA", true, VoiceVariant.Gpu)]
    [InlineData("", true, VoiceVariant.Cpu)]
    [InlineData("nViDiA Corporation", true, VoiceVariant.Gpu)]
    public void ChoosesOnlyNvidiaWithDriver(string providers, bool driver, VoiceVariant expected) =>
        Assert.Equal(expected, VoiceVariantChooser.Choose(providers.Split('|', StringSplitOptions.RemoveEmptyEntries), driver));
}
