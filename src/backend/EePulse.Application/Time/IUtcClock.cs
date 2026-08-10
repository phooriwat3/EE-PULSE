namespace EePulse.Application.Time;

public interface IUtcClock
{
    DateTimeOffset UtcNow { get; }
}
