using Raskladushka.Core.Typing;
using Microsoft.Extensions.Time.Testing;

namespace Raskladushka.Core.Tests;

public class CopyWaitTests
{
    private static CopyWaitResult Run(int[] changes, uint before = 0)
    {
        var time = new FakeTimeProvider();
        var start = time.GetTimestamp();
        return CopyWait.Run(
            () => unchecked(before + (uint)changes.Count(ms => ms <= time.GetElapsedTime(start).TotalMilliseconds)),
            before, new(1000, 200, 2000, 10), time,
            ms => time.Advance(TimeSpan.FromMilliseconds(ms)));
    }

    [Fact]
    public void NoChangesTimesOut()
    {
        Assert.Equal(new(CopyWaitStatus.NothingCopied, -1, -1, 0, 1000), Run([]));
    }

    [Fact]
    public void SingleCopyWaitsForQuiet()
    {
        Assert.Equal(new(CopyWaitStatus.Copied, 20, 20, 1, 220), Run([20]));
    }

    [Fact]
    public void SecondCopyRestartsQuietWindow()
    {
        Assert.Equal(new(CopyWaitStatus.Copied, 20, 70, 2, 270), Run([20, 70]));
    }

    [Fact]
    public void CopyAfterQuietWindowIsNotObserved()
    {
        Assert.Equal(new(CopyWaitStatus.Copied, 20, 20, 1, 220), Run([20, 300]));
    }

    [Fact]
    public void SlowFirstCopyExtendsQuietWindow()
    {
        Assert.Equal(new(CopyWaitStatus.Copied, 400, 700, 2, 1100), Run([400, 700]));
    }

    [Fact]
    public void CopyOnFirstReadIsObserved()
    {
        Assert.Equal(new(CopyWaitStatus.Copied, 0, 0, 1, 200), Run([0]));
    }

    [Fact]
    public void ContinuousChangesReachSettleCap()
    {
        Assert.Equal(new(CopyWaitStatus.Busy, 20, 2020, 21, 2020),
            Run(Enumerable.Range(0, 30).Select(i => 20 + i * 100).ToArray()));
    }

    [Fact]
    public void SequenceWrapCountsAsChange()
    {
        Assert.Equal(new(CopyWaitStatus.Copied, 20, 20, 1, 220), Run([20], uint.MaxValue));
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(100, 500)]
    [InlineData(200, 600)]
    [InlineData(1000, 2000)]
    [InlineData(-1, 500)]
    public void PasteHoldIsClamped(long lastCopyMs, int expected)
    {
        Assert.Equal(expected, CopyWait.PasteHoldMs(lastCopyMs, 500, 2000));
    }
}
