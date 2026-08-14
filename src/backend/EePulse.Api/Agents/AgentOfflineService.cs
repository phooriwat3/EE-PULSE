using EePulse.Application.Time;
using EePulse.Domain.Agents;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EePulse.Api.Agents;

public sealed partial class AgentOfflineService(IServiceScopeFactory scopeFactory, IUtcClock clock, ILogger<AgentOfflineService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
                var agents = await db.Agents.Where(x => x.Status == AgentStatus.Online && x.LastHeartbeatAt != null).ToListAsync(stoppingToken);
                foreach (var agent in agents) agent.MarkOffline(clock.UtcNow);
                var receipts = await db.AgentHeartbeatReceipts.Where(x => x.ReceivedAt < clock.UtcNow.AddHours(-24)).ToListAsync(stoppingToken); db.RemoveRange(receipts);
                if (agents.Count > 0 || receipts.Count > 0) await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { LogProcessingFailure(logger, exception); }
        }
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Error, Message = "Agent offline processing failed")]
    private static partial void LogProcessingFailure(ILogger logger, Exception exception);
}
