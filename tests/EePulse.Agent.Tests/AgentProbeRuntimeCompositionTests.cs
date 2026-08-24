using EePulse.Agent;
using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Outbox;
using EePulse.Agent.Core.Probing;
using EePulse.Agent.Core.Runtime;
using EePulse.Agent.Core.Scheduling;
using EePulse.Contracts.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace EePulse.Agent.Tests;

public sealed class AgentProbeRuntimeCompositionTests
{
    [Fact]
    public async Task RegisteredActiveScheduleSinkPersistsCompletedFakeResultToOutbox()
    {
        var agentId = Guid.NewGuid();
        var outbox = new RecordingOutbox();
        var clock = new ControlledMonotonicClock();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IAgentIdentityStore>(new IdentityStore(Identity(agentId)));
        services.AddSingleton<IProbeResultOutbox>(outbox);
        services.AddAgentProbeRuntime();
        services.AddSingleton<IMonotonicClock>(clock);
        services.AddSingleton<IProbeTransport, SuccessfulProbeTransport>();
        await using var provider = services.BuildServiceProvider();
        var sink = provider.GetRequiredService<IAgentScheduleSink>();
        var coordinator = Assert.IsType<AgentScheduleRuntimeCoordinator>(sink);
        var probeId = Guid.NewGuid();

        await coordinator.ReplaceAsync(Configuration(agentId, probeId), TestContext.Current.CancellationToken);
        await clock.WaitForDelayAsync(TestContext.Current.CancellationToken);
        clock.AdvancePendingDelay();
        var persisted = await outbox.WaitForEnqueueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(agentId, persisted.Envelope.AgentId);
        Assert.Equal(probeId, persisted.Envelope.ProbeId);
        await coordinator.HaltAsync(TestContext.Current.CancellationToken);
    }

    private static AgentIdentity Identity(Guid agentId) => new(agentId, Guid.NewGuid(), Guid.NewGuid(), "agent", "test", [],
        new(Guid.NewGuid(), "test-credential", DateTimeOffset.MaxValue, DateTimeOffset.MaxValue), null, 20, 60, 1);

    private static AgentConfigurationResponse Configuration(Guid agentId, Guid probeId) => new(
        AgentContract.SchemaVersion, agentId, Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, null, ["192.0.2.0/24"],
        [new AgentProbeConfiguration(probeId, Guid.NewGuid(), 1, "icmp", "192.0.2.10", 10, 100, 1, null, null, 1, 1)]);

    private sealed class SuccessfulProbeTransport : IProbeTransport
    {
        public ValueTask<ProbeTransportReply> SendAsync(ProbeTransportRequest request, CancellationToken cancellationToken) =>
            new(new ProbeTransportReply(ProbeTransportStatus.Succeeded, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class IdentityStore(AgentIdentity identity) : IAgentIdentityStore
    {
        public ValueTask<AgentIdentity?> LoadAsync(CancellationToken cancellationToken) => new((AgentIdentity?)identity);
        public ValueTask SaveAsync(AgentIdentity value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingOutbox : IProbeResultOutbox
    {
        private readonly TaskCompletionSource<ProbeResultOutboxRecord> enqueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ProbeResultOutboxRecord> EnqueueAsync(ProbeResultEnvelope envelope, CancellationToken cancellationToken)
        {
            var record = new ProbeResultOutboxRecord(1, envelope, ProbeResultOutboxState.Pending, DateTimeOffset.UnixEpoch, null, null, null, 1);
            enqueued.TrySetResult(record);
            return new(record);
        }
        public Task<ProbeResultOutboxRecord> WaitForEnqueueAsync(CancellationToken cancellationToken) => enqueued.Task.WaitAsync(cancellationToken);
        public ValueTask<IReadOnlyList<ProbeResultOutboxRecord>> ReadPendingAsync(ProbeResultOutboxReadLimit limit, CancellationToken cancellationToken) => new(Array.Empty<ProbeResultOutboxRecord>());
        public ValueTask AcknowledgeAsync(IReadOnlyCollection<Guid> resultIds, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ApplyDeliveryOutcomeAsync(IReadOnlyCollection<Guid> acceptedResultIds, IReadOnlyCollection<ProbeResultPermanentRejection> permanentRejections, DateTimeOffset processedAt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<int> CleanupAcknowledgedAsync(DateTimeOffset cleanupThrough, int maximumCount, CancellationToken cancellationToken) => new(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledMonotonicClock : IMonotonicClock
    {
        private readonly TaskCompletionSource delayEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? pending;
        private TimeSpan pendingDelay;
        private long timestamp;
        public long GetTimestamp() => timestamp;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.FromTicks(endingTimestamp - startingTimestamp);
        public long GetTimestampDelta(TimeSpan duration) => duration.Ticks;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            pendingDelay = delay;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            delayEntered.TrySetResult();
            return new(pending.Task.WaitAsync(cancellationToken));
        }
        public Task WaitForDelayAsync(CancellationToken cancellationToken) => delayEntered.Task.WaitAsync(cancellationToken);
        public void AdvancePendingDelay()
        {
            timestamp += pendingDelay.Ticks;
            pending!.TrySetResult();
        }
    }
}
