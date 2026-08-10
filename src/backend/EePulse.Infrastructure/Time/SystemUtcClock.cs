using EePulse.Application.Time;

namespace EePulse.Infrastructure.Time;

public sealed class SystemUtcClock : IUtcClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
