using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Execution;
using EePulse.Agent.Core.Probing;
using EePulse.Agent.Core.Scheduling;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Core.Runtime;

/// <summary>Owns one cancellable, configuration-versioned set of local probe workers.</summary>
public sealed class AgentScheduleRuntimeCoordinator(
    IMonotonicClock clock,
    LocalProbeRunner runner,
    ProbeAdmissionController admission,
    ILocalProbeResultSink results) : IAgentScheduleSink, IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private Generation? active;

    internal ActiveScheduleGenerationSnapshot Snapshot
    {
        get
        {
            var generation = Volatile.Read(ref active);
            return generation is null
                ? ActiveScheduleGenerationSnapshot.Halted
                : new ActiveScheduleGenerationSnapshot(true, generation.Version, generation.ProbeIds.ToArray());
        }
    }

    public async ValueTask ReplaceAsync(AgentConfigurationResponse configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopActiveAsync().ConfigureAwait(false);
            var generation = new Generation(configuration.AgentId, configuration.ConfigurationVersion, configuration.Probes.Select(probe => probe.ProbeId).ToArray());
            active = generation;
            generation.Workers.AddRange(configuration.Probes.Select(probe => WorkerAsync(generation, probe)));
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async ValueTask HaltAsync(CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopActiveAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task WorkerAsync(Generation generation, AgentProbeConfiguration probe)
    {
        var interval = TimeSpan.FromSeconds(probe.IntervalSeconds);
        var scheduler = new MonotonicSlotScheduler(clock);
        var anchor = clock.GetTimestamp();
        var nextSlot = scheduler.GetInitialFutureSlot(generation.InstallationId, probe.ProbeId, generation.Version, interval);
        while (!generation.Cancellation.IsCancellationRequested && ReferenceEquals(Volatile.Read(ref active), generation))
        {
            var delay = clock.GetElapsedTime(clock.GetTimestamp(), nextSlot);
            if (delay > TimeSpan.Zero)
            {
                await clock.DelayAsync(delay, generation.Cancellation.Token).ConfigureAwait(false);
            }

            var lastSlot = nextSlot;
            generation.Cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(Volatile.Read(ref active), generation) ||
                !admission.TryAcquire(probe.ProbeId, probe.TargetAddress, out var lease))
            {
                nextSlot = scheduler.GetNextFutureSlot(anchor, lastSlot, interval);
                continue;
            }

            using (lease)
            {
                var result = await runner.RunAsync(new LocalProbeExecution(
                    generation.Version, probe.ProbeId, probe.TargetAddress, probe.AttemptCount,
                    TimeSpan.FromMilliseconds(probe.TimeoutMilliseconds), LocalProbeExecution.DefaultInterAttemptDelay),
                    generation.Cancellation.Token).ConfigureAwait(false);
                generation.Cancellation.Token.ThrowIfCancellationRequested();
                if (result is not null && ReferenceEquals(Volatile.Read(ref active), generation))
                {
                    results.Publish(result);
                }
            }

            nextSlot = scheduler.GetNextFutureSlot(anchor, lastSlot, interval);
        }
    }

    private async Task StopActiveAsync()
    {
        var generation = Interlocked.Exchange(ref active, null);
        await StopAsync(generation).ConfigureAwait(false);
    }

    private static async Task StopAsync(Generation? generation)
    {
        if (generation is null) return;
        generation.Cancellation.Cancel();
        try { await Task.WhenAll(generation.Workers).ConfigureAwait(false); }
        catch (OperationCanceledException) when (generation.Cancellation.IsCancellationRequested) { }
        finally { generation.Cancellation.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await HaltAsync(CancellationToken.None).ConfigureAwait(false);
        lifecycle.Dispose();
    }

    private sealed class Generation(Guid installationId, long version, Guid[] probeIds)
    {
        public Guid InstallationId { get; } = installationId;
        public long Version { get; } = version;
        public Guid[] ProbeIds { get; } = probeIds;
        public CancellationTokenSource Cancellation { get; } = new();
        public List<Task> Workers { get; } = [];
    }
}

internal sealed record ActiveScheduleGenerationSnapshot(
    bool IsActive,
    long? ConfigurationVersion,
    IReadOnlyList<Guid> ScheduledProbeIds)
{
    public static ActiveScheduleGenerationSnapshot Halted { get; } = new(false, null, Array.Empty<Guid>());
}
