using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Outbox;

namespace EePulse.Agent;

public sealed partial class ProbeResultDeliveryHost(
    ILogger<ProbeResultDeliveryHost> logger,
    IAgentIdentityStore identityStore,
    ProbeResultDeliveryCoordinator delivery,
    IProbeResultDeliveryDelay delay) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var identity = await identityStore.LoadAsync(stoppingToken).ConfigureAwait(false);
            if (identity is null || identity.IsRevoked)
            {
                await delay.DelayAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                continue;
            }

            var cycle = await delivery.DeliverOnceAsync(identity, stoppingToken).ConfigureAwait(false);
            if (cycle.Delivered)
            {
                LogDelivery(logger);
            }

            await delay.DelayAsync(cycle.NextDelay == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(30) : cycle.NextDelay, stoppingToken)
                .ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Probe-result outbox delivery completed a durable acknowledgement cycle")]
    private static partial void LogDelivery(ILogger logger);
}
