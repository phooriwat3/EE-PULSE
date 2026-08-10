using EePulse.Contracts;
using EePulse.Infrastructure.Time;

namespace EePulse.UnitTests;

public sealed class FoundationTests
{
    [Fact]
    public void CurrentContractVersionIsV1()
    {
        Assert.Equal(1, ApiVersions.Current);
    }

    [Fact]
    public void SystemClockReturnsUtcTime()
    {
        var clock = new SystemUtcClock();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }
}
