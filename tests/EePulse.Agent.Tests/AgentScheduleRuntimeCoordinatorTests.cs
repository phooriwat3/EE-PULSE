using EePulse.Agent.Core.Execution;
using EePulse.Agent.Core.Probing;
using EePulse.Agent.Core.Runtime;
using EePulse.Agent.Core.Scheduling;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Tests;

public sealed class AgentScheduleRuntimeCoordinatorTests
{
    [Fact]
    public async Task ResultCollectorWaitersReevaluateCountsAndCleanUp()
    {
        var sink = new AgentScheduleRuntimeCoordinatorTestHarness.BlockingResultSink(capacity: 4);
        var one = sink.WaitForResultsAsync(1, TestContext.Current.CancellationToken);
        var two = sink.WaitForResultsAsync(2, TestContext.Current.CancellationToken);
        Assert.Equal(2, sink.PendingResultWaiterCount);

        sink.Publish(Result(Guid.Parse("11111111-1111-1111-1111-111111111111")));
        Assert.Single(await one);
        Assert.False(two.IsCompleted);
        Assert.Equal(1, sink.PendingResultWaiterCount);

        sink.Publish(Result(Guid.Parse("22222222-2222-2222-2222-222222222222")));
        Assert.Collection(await two, _ => { }, _ => { });
        Assert.Equal(0, sink.PendingResultWaiterCount);

        using var cancellation = new CancellationTokenSource();
        var cancelled = sink.WaitForResultsAsync(3, cancellation.Token);
        Assert.Equal(1, sink.PendingResultWaiterCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        Assert.Equal(0, sink.PendingResultWaiterCount);
    }

    [Fact]
    public async Task ClockCountWaitersReevaluateIndependentPredicatesAndCleanUp()
    {
        var clock = new AgentScheduleRuntimeCoordinatorTestHarness.ControlledMonotonicClock();
        using var delayCancellation = new CancellationTokenSource();
        var onePending = clock.WaitForPendingDelayCountAsync(1, TestContext.Current.CancellationToken);
        var twoPending = clock.WaitForPendingDelayCountAsync(2, TestContext.Current.CancellationToken);
        var oneCancelled = clock.WaitForCancelledDelayCountAsync(1, TestContext.Current.CancellationToken);

        clock.AdvanceBy(TimeSpan.Zero);
        Assert.False(onePending.IsCompleted);
        Assert.False(twoPending.IsCompleted);
        Assert.False(oneCancelled.IsCompleted);

        var cancelledDelay = clock.DelayAsync(TimeSpan.FromSeconds(1), delayCancellation.Token).AsTask();
        await onePending;
        Assert.False(twoPending.IsCompleted);
        Assert.False(oneCancelled.IsCompleted);

        delayCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledDelay);
        await oneCancelled;
        Assert.False(twoPending.IsCompleted);

        var firstPending = clock.DelayAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).AsTask();
        var secondPending = clock.DelayAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).AsTask();
        await twoPending;

        using var waiterCancellation = new CancellationTokenSource();
        var cancelledWaiter = clock.WaitForPendingDelayCountAsync(3, waiterCancellation.Token);
        Assert.Equal(1, clock.PendingDelayWaiterCount);
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledWaiter);
        Assert.Equal(0, clock.PendingDelayWaiterCount);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await clock.WaitForPendingDelayCountAsync(3, TestContext.Current.CancellationToken));
        Assert.Equal(0, clock.PendingDelayWaiterCount);

        clock.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstPending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await secondPending);
        Assert.Equal(0, clock.TotalWaiterCount);
    }

    [Fact]
    public async Task OneShotCountWaitersCleanUpAfterCancellationTimeoutAndHarnessDisposal()
    {
        var transport = new AgentScheduleRuntimeCoordinatorTestHarness.RecordingProbeTransport(capacity: 4);
        var sink = new AgentScheduleRuntimeCoordinatorTestHarness.BlockingResultSink(capacity: 4);
        using var cancellation = new CancellationTokenSource();

        var transportCancelled = transport.WaitForStartedCountAsync(1, cancellation.Token);
        var sinkCancelled = sink.WaitForEnteredCountAsync(1, cancellation.Token);
        Assert.Equal(1, transport.PendingStartWaiterCount);
        Assert.Equal(1, sink.PendingEnteredWaiterCount);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transportCancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sinkCancelled);
        Assert.Equal(0, transport.PendingStartWaiterCount);
        Assert.Equal(0, sink.PendingEnteredWaiterCount);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await sink.WaitForEnteredCountAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.PendingStartWaiterCount);
        Assert.Equal(0, sink.PendingEnteredWaiterCount);

        transport.Dispose();
        sink.Dispose();
        Assert.Equal(0, transport.TotalWaiterCount);
        Assert.Equal(0, sink.TotalWaiterCount);
    }

    [Fact]
    public async Task HarnessDisposalClearsEveryWaiterCollection()
    {
        var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var clockWaiter = harness.Clock.WaitForPendingDelayCountAsync(1, TestContext.Current.CancellationToken);
        var transportWaiter = harness.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);
        var publicationWaiter = harness.Sink.WaitForEnteredCountAsync(1, TestContext.Current.CancellationToken);

        await harness.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await clockWaiter);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transportWaiter);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await publicationWaiter);
        Assert.Equal(0, harness.Clock.TotalWaiterCount);
        Assert.Equal(0, harness.Transport.TotalWaiterCount);
        Assert.Equal(0, harness.Sink.TotalWaiterCount);
    }

    [Fact]
    public async Task OnlyConfiguredProbeIdsBecomeActiveAndRemovedProbeIdsDisappear()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe();
        var removed = AgentScheduleRuntimeCoordinatorTestHarness.Probe();
        var replacement = AgentScheduleRuntimeCoordinatorTestHarness.Probe();

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first, removed));
        AssertSnapshot(harness, 1, first.ProbeId, removed.ProbeId);

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, replacement));

        AssertSnapshot(harness, 2, replacement.ProbeId);
    }

    [Fact]
    public async Task WorkersAndResultsRemainPinnedToConfigurationVersion()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var probe = AgentScheduleRuntimeCoordinatorTestHarness.Probe();

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(7, probe));
        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);

        var result = await harness.Sink.WaitForResultsAsync(1, TestContext.Current.CancellationToken);
        Assert.Equal(7, result[0].ConfigurationVersion);
        Assert.Equal(probe.ProbeId, result[0].ProbeId);
    }

    [Fact]
    public async Task InitialSlotsUseAcceptedAgentProbeAndConfigurationVersionJitter()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var installation = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var second = AgentScheduleRuntimeCoordinatorTestHarness.Probe(Guid.Parse("baaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.ConfigurationForAgent(7, installation, first, second));
        await harness.Clock.WaitForPendingDelayCountAsync(2, TestContext.Current.CancellationToken);

        var delays = harness.Clock.PendingDelays;
        var expected = StableJitter.ForProbe(installation, first.ProbeId, 7, TimeSpan.FromSeconds(first.IntervalSeconds));
        Assert.Contains(delays, delay => delay.DueTimestamp == expected.Ticks);
        Assert.All(delays, delay => Assert.True(delay.DueTimestamp > harness.Clock.GetTimestamp()));
        Assert.Contains(delays, delay => delay.DueTimestamp != TimeSpan.FromSeconds(first.IntervalSeconds).Ticks);
    }

    [Fact]
    public async Task ReplacementCancelsAndAwaitsSlotWaitingWorkers()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe();
        var second = AgentScheduleRuntimeCoordinatorTestHarness.Probe();

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first));
        await harness.Clock.WaitForPendingDelayCountAsync(1, TestContext.Current.CancellationToken);

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, second));

        Assert.Equal(1, harness.Clock.CancelledDelayCount);
        AssertSnapshot(harness, 2, second.ProbeId);
    }

    [Fact]
    public async Task ReplacementCancelsAndAwaitsBlockedTransport()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        harness.Transport.BlockCompletions = true;
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe();
        var second = AgentScheduleRuntimeCoordinatorTestHarness.Probe();

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first));
        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await harness.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);

        var replace = harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, second)).AsTask();
        await harness.Transport.WaitForCancellationCountAsync(1, TestContext.Current.CancellationToken);
        Assert.False(replace.IsCompleted);

        harness.Transport.ReleaseAll();
        await AgentScheduleRuntimeCoordinatorTestHarness.DeadlockGuard(replace);
        AssertSnapshot(harness, 2, second.ProbeId);
    }

    [Fact]
    public async Task ObsoleteGenerationsCannotDispatchAfterReplacement()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.10");
        var second = AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.11");

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first));
        await harness.Clock.WaitForPendingDelayCountAsync(1, TestContext.Current.CancellationToken);

        var replace = harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, second)).AsTask();
        await harness.Clock.WaitForCancelledDelayCountAsync(1, TestContext.Current.CancellationToken);
        await AgentScheduleRuntimeCoordinatorTestHarness.DeadlockGuard(replace);
        AssertSnapshot(harness, 2, second.ProbeId);

        await harness.Clock.WaitForPendingDelayCountAsync(1, TestContext.Current.CancellationToken);
        harness.Clock.AdvanceBy(TimeSpan.FromSeconds(10));
        await harness.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(harness.Transport.Invocations, call => call.Target == first.TargetAddress);
        Assert.All(harness.Transport.Invocations, call => Assert.Equal(second.TargetAddress, call.Target));
    }

    [Fact]
    public async Task ObsoleteGenerationsCannotPublishAfterReplacement()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        harness.Transport.BlockCompletions = true;
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.10");
        var second = AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.11");

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first));
        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await harness.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);

        var replace = harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, second)).AsTask();
        await harness.Transport.WaitForCancellationCountAsync(1, TestContext.Current.CancellationToken);
        harness.Transport.CompleteCancelledWithSuccess = true;
        harness.Transport.ReleaseAll();
        await AgentScheduleRuntimeCoordinatorTestHarness.DeadlockGuard(replace);

        Assert.DoesNotContain(harness.Sink.Results, result => result.ConfigurationVersion == 1);
    }

    [Fact]
    public async Task ReplaceAsyncWaitsForSynchronousPublicationToExit()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        harness.Sink.BlockPublications = true;
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe();
        var second = AgentScheduleRuntimeCoordinatorTestHarness.Probe();

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first));
        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await harness.Sink.WaitForEnteredCountAsync(1, TestContext.Current.CancellationToken);

        var replace = harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, second)).AsTask();
        Assert.False(replace.IsCompleted);

        harness.Sink.ReleaseAll();
        await AgentScheduleRuntimeCoordinatorTestHarness.DeadlockGuard(replace);
        AssertSnapshot(harness, 2, second.ProbeId);
    }

    [Fact]
    public async Task HaltCancelsSlotWaitsAndBlockedTransport()
    {
        await using var slotHarness = new AgentScheduleRuntimeCoordinatorTestHarness();
        await slotHarness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe()));
        await slotHarness.Clock.WaitForPendingDelayCountAsync(1, TestContext.Current.CancellationToken);
        await slotHarness.HaltAsync();
        Assert.Equal(1, slotHarness.Clock.CancelledDelayCount);

        await using var transportHarness = new AgentScheduleRuntimeCoordinatorTestHarness();
        transportHarness.Transport.BlockCompletions = true;
        await transportHarness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe()));
        await transportHarness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await transportHarness.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);
        var halt = transportHarness.HaltAsync().AsTask();
        await transportHarness.Transport.WaitForCancellationCountAsync(1, TestContext.Current.CancellationToken);
        Assert.False(halt.IsCompleted);
        transportHarness.Transport.ReleaseAll();
        await AgentScheduleRuntimeCoordinatorTestHarness.DeadlockGuard(halt);
    }

    [Fact]
    public async Task NoDispatchOrPublicationAfterHaltAsyncCompletes()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe()));
        await harness.Clock.WaitForPendingDelayCountAsync(1, TestContext.Current.CancellationToken);

        await harness.HaltAsync();
        var dispatches = harness.Transport.Invocations.Count;
        var publications = harness.Sink.Results.Count;
        harness.Clock.AdvanceBy(TimeSpan.FromMinutes(5));

        Assert.Equal(dispatches, harness.Transport.Invocations.Count);
        Assert.Equal(publications, harness.Sink.Results.Count);
        Assert.False(harness.Coordinator.Snapshot.IsActive);
    }

    [Fact]
    public async Task RepeatedHaltAsyncIsIdempotent()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe()));

        await harness.HaltAsync();
        await harness.HaltAsync();

        Assert.False(harness.Coordinator.Snapshot.IsActive);
        Assert.Empty(harness.Coordinator.Snapshot.ScheduledProbeIds);
    }

    [Fact]
    public async Task ConcurrentReplacementsSerializeAndFinalGenerationWins()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe();
        var middle = AgentScheduleRuntimeCoordinatorTestHarness.Probe();
        var final = AgentScheduleRuntimeCoordinatorTestHarness.Probe();

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first));
        await harness.Clock.WaitForPendingDelayCountAsync(1, TestContext.Current.CancellationToken);

        var replaceMiddle = harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, middle)).AsTask();
        var replaceFinal = harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(3, final)).AsTask();

        await AgentScheduleRuntimeCoordinatorTestHarness.DeadlockGuard(Task.WhenAll(replaceMiddle, replaceFinal));
        AssertSnapshot(harness, 3, final.ProbeId);
    }

    [Fact]
    public async Task GlobalTargetAndSameProbeSaturationIsNonQueuing()
    {
        await using var global = new AgentScheduleRuntimeCoordinatorTestHarness(globalConcurrency: 1, targetConcurrency: 2);
        global.Transport.BlockCompletions = true;
        await global.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.10"), AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.11")));
        await global.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await global.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);
        Assert.Single(global.Transport.Invocations);

        await using var target = new AgentScheduleRuntimeCoordinatorTestHarness(globalConcurrency: 2, targetConcurrency: 1);
        target.Transport.BlockCompletions = true;
        await target.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.10"), AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.10")));
        await target.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await target.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);
        Assert.Single(target.Transport.Invocations);

        await using var sameProbe = new AgentScheduleRuntimeCoordinatorTestHarness(globalConcurrency: 2, targetConcurrency: 2);
        sameProbe.Transport.BlockCompletions = true;
        var probeId = Guid.NewGuid();
        await sameProbe.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1,
            AgentScheduleRuntimeCoordinatorTestHarness.Probe(probeId: probeId, target: "192.0.2.10"),
            AgentScheduleRuntimeCoordinatorTestHarness.Probe(probeId: probeId, target: "192.0.2.11")));
        await sameProbe.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await sameProbe.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);
        Assert.Single(sameProbe.Transport.Invocations);
    }

    [Fact]
    public async Task SaturatedOccurrencesAdvanceWithoutCatchUp()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness(globalConcurrency: 1);
        harness.Transport.BlockCompletions = true;
        var blocked = AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.10");
        var saturated = AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.11");
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, blocked, saturated));

        await harness.Clock.WaitForPendingDelayCountAsync(2, TestContext.Current.CancellationToken);
        var initialRegistrationSequence = harness.Clock.DelayRegistrationSequence;
        harness.Clock.AdvanceBy(TimeSpan.FromSeconds(10));
        await harness.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);
        var deniedDelay = await harness.Clock.WaitForPendingDelayRegisteredAfterAsync(initialRegistrationSequence, TestContext.Current.CancellationToken);

        Assert.True(deniedDelay.DueTimestamp > harness.Clock.GetTimestamp());
        Assert.Single(harness.Transport.Invocations);

        harness.Transport.ReleaseAll();
        await harness.Transport.WaitForCompletionCountAsync(1, TestContext.Current.CancellationToken);
        harness.Clock.AdvanceTo(deniedDelay.DueTimestamp);
        await harness.Transport.WaitForStartedCountAsync(2, TestContext.Current.CancellationToken);
        Assert.Collection(harness.Transport.Invocations, _ => { }, _ => { });
    }

    [Fact]
    public async Task TransportCancellationPublishesNoResult()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        harness.Transport.BlockCompletions = true;
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe()));
        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await harness.Transport.WaitForStartedCountAsync(1, TestContext.Current.CancellationToken);

        var halt = harness.HaltAsync().AsTask();
        await harness.Transport.WaitForCancellationCountAsync(1, TestContext.Current.CancellationToken);
        harness.Transport.ReleaseAll();
        await AgentScheduleRuntimeCoordinatorTestHarness.DeadlockGuard(halt);

        Assert.Empty(harness.Sink.Results);
    }

    [Fact]
    public async Task TransportFailurePublishesFrozenErrorAndReleasesPermits()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness(globalConcurrency: 1);
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe(
            probeId: Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            target: "192.0.2.10");
        var second = AgentScheduleRuntimeCoordinatorTestHarness.Probe(
            probeId: Guid.Parse("baaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            target: "192.0.2.11");
        harness.Transport.ThrowOnTargets.Add(first.TargetAddress);
        var installationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var interval = TimeSpan.FromSeconds(first.IntervalSeconds);
        var firstDeadline = StableJitter.ForProbe(installationId, first.ProbeId, 7, interval);
        var secondDeadline = StableJitter.ForProbe(installationId, second.ProbeId, 7, interval);

        Assert.True(firstDeadline < secondDeadline);
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.ConfigurationForAgent(7, installationId, first, second));
        await harness.Clock.WaitForPendingDelayCountAsync(2, TestContext.Current.CancellationToken);
        var initialRegistrationSequence = harness.Clock.DelayRegistrationSequence;

        harness.Clock.AdvanceTo(firstDeadline.Ticks);
        var firstResult = await harness.Sink.WaitForResultsAsync(1, TestContext.Current.CancellationToken);
        var failed = Assert.Single(firstResult, result => result.ProbeId == first.ProbeId);
        Assert.Equal(ProbeErrorCategory.TransportError, failed.ErrorCategory);
        Assert.Equal(7, failed.ConfigurationVersion);
        Assert.Equal(first.ProbeId, failed.ProbeId);

        var nextDelay = await harness.Clock.WaitForPendingDelayRegisteredAfterAsync(
            initialRegistrationSequence,
            TestContext.Current.CancellationToken);
        Assert.True(nextDelay.DueTimestamp > secondDeadline.Ticks);

        harness.Clock.AdvanceTo(secondDeadline.Ticks);
        var results = await harness.Sink.WaitForResultsAsync(2, TestContext.Current.CancellationToken);
        var succeeded = Assert.Single(results, result => result.ProbeId == second.ProbeId);
        Assert.Null(succeeded.ErrorCategory);
        Assert.Equal(7, succeeded.ConfigurationVersion);
        Assert.Equal(second.ProbeId, succeeded.ProbeId);
        Assert.Single(harness.Transport.Invocations, call => call.Target == first.TargetAddress);
        Assert.Single(harness.Transport.Invocations, call => call.Target == second.TargetAddress);
    }

    [Fact]
    public async Task ResultSinkExceptionsAreNotTransportErrorAndAreNotRetried()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        harness.Sink.ThrowOnPublish = true;
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe()));

        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await harness.Sink.WaitForEnteredCountAsync(1, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await harness.HaltAsync());

        Assert.Equal("fake sink failure", error.Message);
        Assert.Single(harness.Transport.Invocations);
        Assert.Empty(harness.Sink.Results);
        Assert.Equal(1, harness.Sink.PublishAttempts);
        Assert.DoesNotContain(harness.Sink.Results, result => result.ErrorCategory == ProbeErrorCategory.TransportError);
        Assert.False(harness.Coordinator.Snapshot.IsActive);
        Assert.Empty(harness.Coordinator.Snapshot.ScheduledProbeIds);

        harness.Clock.AdvanceBy(TimeSpan.FromSeconds(10));
        Assert.Single(harness.Transport.Invocations);
        Assert.Equal(1, harness.Sink.PublishAttempts);
        await harness.HaltAsync();
    }

    [Fact]
    public async Task ReplaceAsyncRethrowsSinkFaultWithoutStartingReplacementAndLaterReplaceStartsCleanly()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.10");
        var replacement = AgentScheduleRuntimeCoordinatorTestHarness.Probe(target: "192.0.2.11");
        harness.Sink.ThrowOnPublish = true;
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first));

        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await harness.Sink.WaitForEnteredCountAsync(1, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, replacement)));

        Assert.False(harness.Coordinator.Snapshot.IsActive);
        Assert.Empty(harness.Coordinator.Snapshot.ScheduledProbeIds);
        Assert.DoesNotContain(harness.Transport.Invocations, call => call.Target == replacement.TargetAddress);

        harness.Sink.ThrowOnPublish = false;
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, replacement));
        AssertSnapshot(harness, 2, replacement.ProbeId);
    }

    [Fact]
    public async Task RepeatedReplacementAndHaltLeavesInactiveEmptySnapshotAndNoActivity()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, AgentScheduleRuntimeCoordinatorTestHarness.Probe()));
        await harness.HaltAsync();
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, AgentScheduleRuntimeCoordinatorTestHarness.Probe()));
        await harness.HaltAsync();
        await harness.HaltAsync();

        var dispatches = harness.Transport.Invocations.Count;
        var publications = harness.Sink.Results.Count;
        harness.Clock.AdvanceBy(TimeSpan.FromMinutes(1));

        Assert.False(harness.Coordinator.Snapshot.IsActive);
        Assert.Empty(harness.Coordinator.Snapshot.ScheduledProbeIds);
        Assert.Equal(dispatches, harness.Transport.Invocations.Count);
        Assert.Equal(publications, harness.Sink.Results.Count);
    }

    [Fact]
    public async Task PublishedResultsNeverMixConfigurationVersions()
    {
        await using var harness = new AgentScheduleRuntimeCoordinatorTestHarness();
        var first = AgentScheduleRuntimeCoordinatorTestHarness.Probe();
        var second = AgentScheduleRuntimeCoordinatorTestHarness.Probe();

        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(1, first));
        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        await harness.Sink.WaitForResultsAsync(1, TestContext.Current.CancellationToken);
        await harness.ReplaceAsync(AgentScheduleRuntimeCoordinatorTestHarness.Configuration(2, second));
        await harness.AdvanceSlotsAsync(1, TestContext.Current.CancellationToken);
        var results = await harness.Sink.WaitForResultsAsync(2, TestContext.Current.CancellationToken);

        Assert.All(results.Where(result => result.ProbeId == first.ProbeId), result => Assert.Equal(1, result.ConfigurationVersion));
        Assert.All(results.Where(result => result.ProbeId == second.ProbeId), result => Assert.Equal(2, result.ConfigurationVersion));
    }

    private static void AssertSnapshot(AgentScheduleRuntimeCoordinatorTestHarness harness, long version, params Guid[] probeIds)
    {
        Assert.True(harness.Coordinator.Snapshot.IsActive);
        Assert.Equal(version, harness.Coordinator.Snapshot.ConfigurationVersion);
        Assert.Equal(probeIds.OrderBy(id => id), harness.Coordinator.Snapshot.ScheduledProbeIds.OrderBy(id => id));
    }

    private static LocalProbeResult Result(Guid probeId) => new(
        1, probeId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        1, 1, 0, 1, 1, 1, null);

}

internal sealed class AgentScheduleRuntimeCoordinatorTestHarness : IAsyncDisposable
    {
        private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(10);

        public AgentScheduleRuntimeCoordinatorTestHarness(int globalConcurrency = 64, int targetConcurrency = 1)
        {
            Clock = new ControlledMonotonicClock();
            Transport = new RecordingProbeTransport(capacity: 64);
            ExecutionClock = new ProbeExecutionClock();
            Sink = new BlockingResultSink(capacity: 64);
            Coordinator = new AgentScheduleRuntimeCoordinator(
                Clock,
                new LocalProbeRunner(Transport, ExecutionClock),
                new ProbeAdmissionController(globalConcurrency, targetConcurrency),
                Sink);
        }

        public ControlledMonotonicClock Clock { get; }
        public RecordingProbeTransport Transport { get; }
        public ProbeExecutionClock ExecutionClock { get; }
        public BlockingResultSink Sink { get; }
        public AgentScheduleRuntimeCoordinator Coordinator { get; }

        public static async Task DeadlockGuard(Task task) => await task.WaitAsync(GuardTimeout);

        public ValueTask ReplaceAsync(AgentConfigurationResponse configuration) =>
            Coordinator.ReplaceAsync(configuration, TestContext.Current.CancellationToken);

        public ValueTask HaltAsync() => Coordinator.HaltAsync(TestContext.Current.CancellationToken);

        public async Task AdvanceSlotsAsync(int count, CancellationToken cancellationToken)
        {
            for (var index = 0; index < count; index++)
            {
                await Clock.WaitForPendingDelayCountAsync(index + 1, cancellationToken);
                Clock.AdvanceBy(TimeSpan.FromSeconds(10));
            }
        }

        public static AgentConfigurationResponse Configuration(long version, params AgentProbeConfiguration[] probes) =>
            ConfigurationForAgent(version, Guid.Parse("11111111-1111-1111-1111-111111111111"), probes);

        public static AgentConfigurationResponse ConfigurationForAgent(long version, Guid agentId, params AgentProbeConfiguration[] probes) => new(
            AgentContract.SchemaVersion,
            agentId,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            version,
            DateTimeOffset.UnixEpoch,
            null,
            ["192.0.2.0/24"],
            probes);

        public static AgentProbeConfiguration Probe(Guid? probeId = null, string target = "192.0.2.10", int intervalSeconds = 10) => new(
            probeId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "icmp",
            target,
            intervalSeconds,
            100,
            1,
            null,
            null,
            1,
            1);

        public async ValueTask DisposeAsync()
        {
            Sink.ReleaseAll();
            Transport.ReleaseAll();
            Clock.CancelAll();
            await Coordinator.HaltAsync(CancellationToken.None).AsTask().WaitAsync(GuardTimeout);
            Sink.ReleaseAll();
            Transport.ReleaseAll();
            Clock.CancelAll();
            Sink.Dispose();
            Transport.Dispose();
            Clock.Dispose();
        }

        public sealed class ControlledMonotonicClock : IMonotonicClock
        {
            private readonly object gate = new();
            private readonly List<PendingDelay> pending = [];
            private readonly List<CountWaiter> pendingDelayWaiters = [];
            private readonly List<CountWaiter> cancelledDelayWaiters = [];
            private readonly List<DelayRegistrationWaiter> registrationWaiters = [];
            private long timestamp;
            private long delayRegistrationSequence;

            public int CancelledDelayCount { get; private set; }
            public long DelayRegistrationSequence { get { lock (gate) return delayRegistrationSequence; } }
            public IReadOnlyList<DelayRegistration> PendingDelays { get { lock (gate) return pending.Select(delay => delay.Registration).ToArray(); } }
            public int PendingDelayWaiterCount { get { lock (gate) return pendingDelayWaiters.Count; } }
            public int CancelledDelayWaiterCount { get { lock (gate) return cancelledDelayWaiters.Count; } }
            public int TotalWaiterCount { get { lock (gate) return pendingDelayWaiters.Count + cancelledDelayWaiters.Count + registrationWaiters.Count; } }

            public long GetTimestamp()
            {
                lock (gate) return timestamp;
            }

            public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
                TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

            public long GetTimestampDelta(TimeSpan duration)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
                return duration.Ticks;
            }

            public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
                if (delay == TimeSpan.Zero) return ValueTask.CompletedTask;
                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationTokenRegistration registration = default;
                long sequence;
                lock (gate)
                {
                    sequence = checked(++delayRegistrationSequence);
                }

                var pendingDelay = new PendingDelay(
                    new DelayRegistration(sequence, checked(GetTimestamp() + delay.Ticks)),
                    source,
                    () => registration.Dispose());
                registration = cancellationToken.Register(() =>
                {
                    lock (gate)
                    {
                        if (pending.Remove(pendingDelay))
                        {
                            CancelledDelayCount++;
                        }
                    }

                    pendingDelay.DisposeRegistration();
                    source.TrySetCanceled(cancellationToken);
                    ReleaseSatisfiedWaiters();
                });

                lock (gate)
                {
                    pending.Add(pendingDelay);
                }

                ReleaseRegisteredWaiters(pendingDelay.Registration);
                ReleaseSatisfiedWaiters();

                return new ValueTask(source.Task);
            }

            public void AdvanceBy(TimeSpan duration)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
                PendingDelay[] due;
                lock (gate)
                {
                    timestamp = checked(timestamp + duration.Ticks);
                    due = pending.Where(delay => delay.Registration.DueTimestamp <= timestamp).ToArray();
                    foreach (var delay in due) pending.Remove(delay);
                }

                foreach (var delay in due)
                {
                    delay.DisposeRegistration();
                    delay.Source.TrySetResult();
                }

                ReleaseSatisfiedWaiters();
            }

            public Task WaitForPendingDelayCountAsync(int count, CancellationToken cancellationToken) =>
                WaitForCountAsync(pendingDelayWaiters, () => pending.Count, count, cancellationToken);

            public DelayRegistration GetEarliestPendingDelay()
            {
                lock (gate)
                {
                    return pending
                        .OrderBy(delay => delay.Registration.DueTimestamp)
                        .ThenBy(delay => delay.Registration.Sequence)
                        .First()
                        .Registration;
                }
            }

            public void AdvanceTo(long dueTimestamp)
            {
                long current;
                lock (gate) current = timestamp;
                ArgumentOutOfRangeException.ThrowIfLessThan(dueTimestamp, current);
                AdvanceBy(TimeSpan.FromTicks(dueTimestamp - current));
            }

            public async Task<DelayRegistration> WaitForPendingDelayRegisteredAfterAsync(long sequence, CancellationToken cancellationToken)
            {
                DelayRegistrationWaiter? waiter = null;
                lock (gate)
                {
                    var registered = pending
                        .Select(delay => delay.Registration)
                        .Where(registration => registration.Sequence > sequence)
                        .OrderBy(registration => registration.Sequence)
                        .FirstOrDefault();
                    if (registered is not null) return registered;

                    waiter = new DelayRegistrationWaiter(
                        sequence,
                        new TaskCompletionSource<DelayRegistration>(TaskCreationOptions.RunContinuationsAsynchronously));
                    registrationWaiters.Add(waiter);
                }

                try
                {
                    return await waiter.Source.Task.WaitAsync(GuardTimeout, cancellationToken);
                }
                finally
                {
                    lock (gate) registrationWaiters.Remove(waiter);
                }
            }

            public Task WaitForCancelledDelayCountAsync(int count, CancellationToken cancellationToken) =>
                WaitForCountAsync(cancelledDelayWaiters, () => CancelledDelayCount, count, cancellationToken);

            public void CancelAll()
            {
                PendingDelay[] delays;
                lock (gate)
                {
                    delays = pending.ToArray();
                    pending.Clear();
                }

                foreach (var delay in delays)
                {
                    delay.DisposeRegistration();
                    delay.Source.TrySetCanceled();
                }

                ReleaseSatisfiedWaiters();
            }

            public void Dispose()
            {
                PendingDelay[] pendingDelays;
                CountWaiter[] countWaiters;
                DelayRegistrationWaiter[] sequenceWaiters;
                lock (gate)
                {
                    pendingDelays = pending.ToArray();
                    pending.Clear();
                    countWaiters = pendingDelayWaiters.Concat(cancelledDelayWaiters).ToArray();
                    pendingDelayWaiters.Clear();
                    cancelledDelayWaiters.Clear();
                    sequenceWaiters = registrationWaiters.ToArray();
                    registrationWaiters.Clear();
                }

                foreach (var delay in pendingDelays)
                {
                    delay.DisposeRegistration();
                    delay.Source.TrySetCanceled();
                }

                foreach (var waiter in countWaiters) waiter.Source.TrySetCanceled();
                foreach (var waiter in sequenceWaiters) waiter.Source.TrySetCanceled();
            }

            private async Task WaitForCountAsync(
                List<CountWaiter> waiters,
                Func<int> currentCount,
                int count,
                CancellationToken cancellationToken)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
                CountWaiter? waiter = null;
                lock (gate)
                {
                    if (currentCount() >= count) return;
                    waiter = new CountWaiter(count, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    waiters.Add(waiter);
                }

                try
                {
                    await waiter.Source.Task.WaitAsync(GuardTimeout, cancellationToken);
                }
                finally
                {
                    lock (gate) waiters.Remove(waiter);
                }
            }

            private void ReleaseSatisfiedWaiters()
            {
                CountWaiter[] pendingReady;
                CountWaiter[] cancelledReady;
                lock (gate)
                {
                    pendingReady = TakeSatisfiedWaiters(pendingDelayWaiters, pending.Count);
                    cancelledReady = TakeSatisfiedWaiters(cancelledDelayWaiters, CancelledDelayCount);
                }

                Complete(pendingReady);
                Complete(cancelledReady);
            }

            private static CountWaiter[] TakeSatisfiedWaiters(List<CountWaiter> waiters, int currentCount)
            {
                var ready = waiters.Where(waiter => currentCount >= waiter.RequiredCount).ToArray();
                foreach (var waiter in ready) waiters.Remove(waiter);
                return ready;
            }

            private static void Complete(IEnumerable<CountWaiter> waiters)
            {
                foreach (var waiter in waiters) waiter.Source.TrySetResult();
            }

            private void ReleaseRegisteredWaiters(DelayRegistration registration)
            {
                DelayRegistrationWaiter[] ready;
                lock (gate)
                {
                    ready = registrationWaiters
                        .Where(waiter => registration.Sequence > waiter.AfterSequence)
                        .ToArray();
                    foreach (var waiter in ready) registrationWaiters.Remove(waiter);
                }

                foreach (var waiter in ready) waiter.Source.TrySetResult(registration);
            }

            public sealed record DelayRegistration(long Sequence, long DueTimestamp);

            private sealed record DelayRegistrationWaiter(
                long AfterSequence,
                TaskCompletionSource<DelayRegistration> Source);

            private sealed record CountWaiter(int RequiredCount, TaskCompletionSource Source);

            private sealed record PendingDelay(
                DelayRegistration Registration,
                TaskCompletionSource Source,
                Action DisposeRegistration);
        }

        public sealed class ProbeExecutionClock : IProbeExecutionClock
        {
            private long timestamp;
            public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;

            public DateTimeOffset GetUtcNow() => UtcNow;

            public long GetTimestamp() => timestamp;

            public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
                TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

            public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                UtcNow += delay;
                timestamp += delay.Ticks;
                return ValueTask.CompletedTask;
            }
        }

        public sealed class RecordingProbeTransport(int capacity) : IProbeTransport
        {
            private readonly object gate = new();
            private readonly List<Invocation> invocations = [];
            private readonly List<CountWaiter> startWaiters = [];
            private readonly List<CountWaiter> cancelWaiters = [];
            private readonly List<CountWaiter> completionWaiters = [];
            private readonly List<TaskCompletionSource<ProbeTransportReply>> blocked = [];

            public bool BlockCompletions { get; set; }
            public bool CompleteCancelledWithSuccess { get; set; }
            public HashSet<string> ThrowOnTargets { get; } = new(StringComparer.Ordinal);
            public IReadOnlyList<Invocation> Invocations { get { lock (gate) return invocations.ToArray(); } }
            public int CancellationCount { get; private set; }
            public int CompletionCount { get; private set; }
            public int PendingStartWaiterCount { get { lock (gate) return startWaiters.Count; } }
            public int TotalWaiterCount { get { lock (gate) return startWaiters.Count + cancelWaiters.Count + completionWaiters.Count; } }

            public ValueTask<ProbeTransportReply> SendAsync(ProbeTransportRequest request, CancellationToken cancellationToken)
            {
                TaskCompletionSource<ProbeTransportReply>? blockedCompletion = null;
                CountWaiter[] started;
                var throws = false;
                lock (gate)
                {
                    if (invocations.Count >= capacity) throw new InvalidOperationException("Fake transport capacity exceeded.");
                    invocations.Add(new Invocation(request.Target));
                    started = TakeSatisfiedWaiters(startWaiters, invocations.Count);
                    throws = ThrowOnTargets.Contains(request.Target);
                    if (!throws && BlockCompletions)
                    {
                        blockedCompletion = new TaskCompletionSource<ProbeTransportReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                        blocked.Add(blockedCompletion);
                    }
                }

                Complete(started);
                if (throws) throw new InvalidOperationException("frozen fake transport failure");

                if (blockedCompletion is null)
                {
                    MarkCompleted();
                    return ValueTask.FromResult(new ProbeTransportReply(ProbeTransportStatus.Succeeded, TimeSpan.FromMilliseconds(1)));
                }

                cancellationToken.Register(() =>
                {
                    CountWaiter[] cancelled;
                    lock (gate)
                    {
                        CancellationCount++;
                        cancelled = TakeSatisfiedWaiters(cancelWaiters, CancellationCount);
                    }

                    Complete(cancelled);

                    if (!CompleteCancelledWithSuccess)
                    {
                        blockedCompletion.TrySetCanceled(cancellationToken);
                    }
                });

                return AwaitBlocked(blockedCompletion);
            }

            public void ReleaseAll()
            {
                TaskCompletionSource<ProbeTransportReply>[] completions;
                lock (gate)
                {
                    completions = blocked.ToArray();
                    blocked.Clear();
                }

                foreach (var completion in completions)
                {
                    completion.TrySetResult(new ProbeTransportReply(ProbeTransportStatus.Succeeded, TimeSpan.FromMilliseconds(1)));
                }
            }

            public Task WaitForStartedCountAsync(int count, CancellationToken cancellationToken) =>
                WaitForCountAsync(startWaiters, () => invocations.Count, count, cancellationToken);

            public Task WaitForCancellationCountAsync(int count, CancellationToken cancellationToken) =>
                WaitForCountAsync(cancelWaiters, () => CancellationCount, count, cancellationToken);

            public Task WaitForCompletionCountAsync(int count, CancellationToken cancellationToken) =>
                WaitForCountAsync(completionWaiters, () => CompletionCount, count, cancellationToken);

            private async ValueTask<ProbeTransportReply> AwaitBlocked(TaskCompletionSource<ProbeTransportReply> completion)
            {
                try
                {
                    return await completion.Task.ConfigureAwait(false);
                }
                finally
                {
                    MarkCompleted();
                }
            }

            private void MarkCompleted()
            {
                CountWaiter[] completed;
                lock (gate)
                {
                    CompletionCount++;
                    completed = TakeSatisfiedWaiters(completionWaiters, CompletionCount);
                }

                Complete(completed);
            }

            public void Dispose()
            {
                CountWaiter[] waiters;
                lock (gate)
                {
                    waiters = startWaiters.Concat(cancelWaiters).Concat(completionWaiters).ToArray();
                    startWaiters.Clear();
                    cancelWaiters.Clear();
                    completionWaiters.Clear();
                }

                foreach (var waiter in waiters) waiter.Source.TrySetCanceled();
            }

            private async Task WaitForCountAsync(
                List<CountWaiter> waiters,
                Func<int> current,
                int count,
                CancellationToken cancellationToken)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
                CountWaiter? waiter = null;
                lock (gate)
                {
                    if (current() >= count) return;
                    waiter = new CountWaiter(count, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    waiters.Add(waiter);
                }

                try
                {
                    await waiter.Source.Task.WaitAsync(GuardTimeout, cancellationToken);
                }
                finally
                {
                    lock (gate) waiters.Remove(waiter);
                }
            }

            private static CountWaiter[] TakeSatisfiedWaiters(List<CountWaiter> waiters, int currentCount)
            {
                var ready = waiters.Where(waiter => currentCount >= waiter.RequiredCount).ToArray();
                foreach (var waiter in ready) waiters.Remove(waiter);
                return ready;
            }

            private static void Complete(IEnumerable<CountWaiter> waiters)
            {
                foreach (var waiter in waiters) waiter.Source.TrySetResult();
            }

            public sealed record Invocation(string Target);

            private sealed record CountWaiter(int RequiredCount, TaskCompletionSource Source);
        }

        public sealed class BlockingResultSink(int capacity) : ILocalProbeResultSink
        {
            private readonly object gate = new();
            private readonly List<LocalProbeResult> results = [];
            private readonly List<ResultWaiter> resultWaiters = [];
            private readonly List<CountWaiter> enteredWaiters = [];
            private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool BlockPublications { get; set; }
            public bool ThrowOnPublish { get; set; }
            public int PublishAttempts { get; private set; }
            public IReadOnlyList<LocalProbeResult> Results { get { lock (gate) return results.ToArray(); } }
            public int PendingResultWaiterCount { get { lock (gate) return resultWaiters.Count; } }
            public int PendingEnteredWaiterCount { get { lock (gate) return enteredWaiters.Count; } }
            public int TotalWaiterCount { get { lock (gate) return resultWaiters.Count + enteredWaiters.Count; } }

            public void Publish(LocalProbeResult result)
            {
                CountWaiter[] entered;
                var throws = false;
                lock (gate)
                {
                    PublishAttempts++;
                    entered = TakeSatisfiedWaiters(enteredWaiters, PublishAttempts);
                    throws = ThrowOnPublish;
                }

                Complete(entered);
                if (throws) throw new InvalidOperationException("fake sink failure");

                if (BlockPublications)
                {
                    release.Task.GetAwaiter().GetResult();
                }

                lock (gate)
                {
                    if (results.Count >= capacity) throw new InvalidOperationException("Fake result sink capacity exceeded.");
                    results.Add(result);
                    ReleaseResultWaiters();
                }
            }

            public void ReleaseAll() => release.TrySetResult();

            public async Task<IReadOnlyList<LocalProbeResult>> WaitForResultsAsync(
                int count,
                CancellationToken cancellationToken = default)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
                ResultWaiter? waiter = null;
                lock (gate)
                {
                    if (results.Count >= count) return results.ToArray();
                    waiter = new ResultWaiter(count, new TaskCompletionSource<IReadOnlyList<LocalProbeResult>>(
                        TaskCreationOptions.RunContinuationsAsynchronously));
                    resultWaiters.Add(waiter);
                }

                try
                {
                    return await waiter.Source.Task.WaitAsync(GuardTimeout, cancellationToken);
                }
                finally
                {
                    lock (gate) resultWaiters.Remove(waiter);
                }
            }

            public Task WaitForEnteredCountAsync(int count, CancellationToken cancellationToken) =>
                WaitForCountAsync(enteredWaiters, () => PublishAttempts, count, cancellationToken);

            public void Dispose()
            {
                ResultWaiter[] resultsToCancel;
                CountWaiter[] enteredToCancel;
                lock (gate)
                {
                    resultsToCancel = resultWaiters.ToArray();
                    resultWaiters.Clear();
                    enteredToCancel = enteredWaiters.ToArray();
                    enteredWaiters.Clear();
                }

                ReleaseAll();
                foreach (var waiter in resultsToCancel) waiter.Source.TrySetCanceled();
                foreach (var waiter in enteredToCancel) waiter.Source.TrySetCanceled();
            }

            private async Task WaitForCountAsync(
                List<CountWaiter> waiters,
                Func<int> current,
                int count,
                CancellationToken cancellationToken)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
                CountWaiter? waiter = null;
                lock (gate)
                {
                    if (current() >= count) return;
                    waiter = new CountWaiter(count, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    waiters.Add(waiter);
                }

                try
                {
                    await waiter.Source.Task.WaitAsync(GuardTimeout, cancellationToken);
                }
                finally
                {
                    lock (gate) waiters.Remove(waiter);
                }
            }

            private void ReleaseResultWaiters()
            {
                var ready = resultWaiters.Where(waiter => results.Count >= waiter.RequiredCount).ToArray();
                foreach (var waiter in ready) resultWaiters.Remove(waiter);
                var snapshot = results.ToArray();
                foreach (var waiter in ready) waiter.Source.TrySetResult(snapshot);
            }

            private static CountWaiter[] TakeSatisfiedWaiters(List<CountWaiter> waiters, int currentCount)
            {
                var ready = waiters.Where(waiter => currentCount >= waiter.RequiredCount).ToArray();
                foreach (var waiter in ready) waiters.Remove(waiter);
                return ready;
            }

            private static void Complete(IEnumerable<CountWaiter> waiters)
            {
                foreach (var waiter in waiters) waiter.Source.TrySetResult();
            }

            private sealed record ResultWaiter(
                int RequiredCount,
                TaskCompletionSource<IReadOnlyList<LocalProbeResult>> Source);

            private sealed record CountWaiter(int RequiredCount, TaskCompletionSource Source);
        }
}
