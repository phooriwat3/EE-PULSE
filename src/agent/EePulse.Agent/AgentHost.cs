namespace EePulse.Agent;

public sealed partial class AgentHost(
    ILogger<AgentHost> logger,
    EePulse.Agent.Core.Runtime.AgentRuntime runtime,
    EePulse.Agent.Core.Identity.IAgentIdentityStore identityStore,
    IHostEnvironment environment) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);

        try
        {
            var identity = await identityStore.LoadAsync(stoppingToken);
            if (identity is null)
            {
                if (environment.IsProduction())
                {
                    throw new InvalidOperationException("Agent enrollment and protected identity are required in Production.");
                }

                LogEnrollmentRequired(logger);
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
                return;
            }

            await runtime.RunAsync(stoppingToken);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent enrollment is required; no probing or upload work will start")]
    private static partial void LogEnrollmentRequired(ILogger logger);
}
