using Raskladushka.Core.Dictation;

namespace Raskladushka.Core.Tests;

public sealed class AudioStatsTests
{
    [Fact]
    public void ComputesNormalizedRms()
    {
        Assert.Equal(0, AudioStats.Rms([]));
        Assert.Equal(0, AudioStats.Rms(new short[100]));
        Assert.InRange(AudioStats.Rms([short.MaxValue, short.MaxValue]), 0.9999, 1);
        Assert.Equal(1, AudioStats.Rms([short.MinValue]));
        Assert.Equal(Math.Sqrt(0.125), AudioStats.Rms([16384, 0]), 10);
    }
}
