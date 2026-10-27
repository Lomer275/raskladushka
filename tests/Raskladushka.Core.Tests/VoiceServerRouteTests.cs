using Microsoft.Extensions.Time.Testing;
using Raskladushka.Core.Dictation;

namespace Raskladushka.Core.Tests;

public sealed class VoiceServerRouteTests
{
    [Theory(DisplayName = "Р1 TimeoutFor")]
    [InlineData(0, 3)]
    [InlineData(20, 5)]
    [InlineData(120, 15)]
    public void TimeoutFor(double audio, double expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), VoiceServerRoute.TimeoutFor(TimeSpan.FromSeconds(audio)));

    [Fact(DisplayName = "Р2 Cooldown boundary")]
    public void CooldownBoundary()
    {
        var time = new FakeTimeProvider();
        var route = new VoiceServerRoute(time);
        Assert.Null(route.OnOutcome(RemoteOutcome.Ok));
        Assert.False(route.InCooldown);
        Assert.Equal(VoiceServerRoute.ServerUnavailableNotice, route.OnOutcome(RemoteOutcome.Timeout));
        Assert.True(route.InCooldown);
        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.True(route.InCooldown);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(route.InCooldown);
    }

    [Fact(DisplayName = "Р3 Rejected key")]
    public void RejectedKey()
    {
        var route = new VoiceServerRoute(new FakeTimeProvider());
        Assert.Equal(VoiceServerRoute.KeyRejectedNotice, route.OnOutcome(RemoteOutcome.KeyRejected));
        Assert.True(route.InCooldown);
    }
}
