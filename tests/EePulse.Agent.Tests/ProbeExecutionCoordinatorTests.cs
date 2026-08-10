using EePulse.Agent.Core.Execution;

namespace EePulse.Agent.Tests;

public sealed class ProbeExecutionCoordinatorTests
{
    [Fact]
    public async Task ConcurrentRunForSameProbeIsRejected()
    {
        using var coordinator = new ProbeExecutionCoordinator(maximumConcurrency: 2);
        var probeId = Guid.NewGuid();
        var entered = NewSignal();
        var release = NewSignal();

        var firstRun = coordinator.TryExecuteAsync(
            probeId,
            async cancellationToken =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            },
            TestContext.Current.CancellationToken).AsTask();

        await entered.Task;

        var secondRan = await coordinator.TryExecuteAsync(
            probeId,
            _ => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken);

        release.SetResult();

        Assert.False(secondRan);
        Assert.True(await firstRun);
    }

    [Fact]
    public async Task GlobalConcurrencyLimitIsEnforcedAcrossProbes()
    {
        using var coordinator = new ProbeExecutionCoordinator(maximumConcurrency: 1);
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var secondEntered = NewSignal();

        var firstRun = coordinator.TryExecuteAsync(
            Guid.NewGuid(),
            async cancellationToken =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            },
            TestContext.Current.CancellationToken).AsTask();

        await firstEntered.Task;

        var secondRun = coordinator.TryExecuteAsync(
            Guid.NewGuid(),
            _ =>
            {
                secondEntered.SetResult();
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken).AsTask();

        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.SetResult();

        Assert.True(await firstRun);
        Assert.True(await secondRun);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellationWhileWaitingReleasesProbeLease()
    {
        using var coordinator = new ProbeExecutionCoordinator(maximumConcurrency: 1);
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var waitingProbeId = Guid.NewGuid();

        var firstRun = coordinator.TryExecuteAsync(
            Guid.NewGuid(),
            async cancellationToken =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            },
            TestContext.Current.CancellationToken).AsTask();

        await firstEntered.Task;
        using var cancellation = new CancellationTokenSource();
        var cancelledRun = coordinator.TryExecuteAsync(
            waitingProbeId,
            _ => ValueTask.CompletedTask,
            cancellation.Token).AsTask();

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRun);

        releaseFirst.SetResult();
        Assert.True(await firstRun);
        Assert.True(await coordinator.TryExecuteAsync(
            waitingProbeId,
            _ => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
