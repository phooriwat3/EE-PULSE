namespace EePulse.Agent;

public sealed partial class AgentHost(ILogger<AgentHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogStopping(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "EE Pulse Probe Agent host started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "EE Pulse Probe Agent host stopping")]
    private static partial void LogStopping(ILogger logger);
}
