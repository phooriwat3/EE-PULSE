using EePulse.Agent.Core.Scheduling;

namespace EePulse.Agent.Tests;

public sealed class StableJitterTests
{
    [Fact]
    public void SameProbeAndIntervalProduceSameOffset()
    {
        var probeId = Guid.Parse("3a7df0a7-f568-4b42-a9fa-4e38b6f26cd5");
        var interval = TimeSpan.FromSeconds(30);

        var first = StableJitter.ForProbe(probeId, interval);
        var second = StableJitter.ForProbe(probeId, interval);

        Assert.Equal(first, second);
        Assert.InRange(first, TimeSpan.Zero, interval - TimeSpan.FromTicks(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveIntervalIsRejected(int intervalTicks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StableJitter.ForProbe(Guid.Empty, TimeSpan.FromTicks(intervalTicks)));
    }
}
