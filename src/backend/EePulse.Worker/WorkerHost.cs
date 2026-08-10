namespace EePulse.Worker;

public sealed partial class WorkerHost(ILogger<WorkerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "EE Pulse application worker started")]
    private static partial void LogStarted(ILogger logger);
}
