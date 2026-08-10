namespace EePulse.Agent;

public sealed partial class AgentHost(ILogger<AgentHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "EE Pulse Probe Agent host started")]
    private static partial void LogStarted(ILogger logger);
}
