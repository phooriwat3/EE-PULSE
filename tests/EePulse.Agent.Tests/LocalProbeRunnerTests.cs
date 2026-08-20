using EePulse.Agent.Core.Probing;

namespace EePulse.Agent.Tests;

public sealed class LocalProbeRunnerTests
{
    [Fact]
    public async Task SequentialAttemptsProduceImmutableAggregatesAndSpacing()
    {
        var clock = new FakeClock();
        var transport = new FakeProbeTransport(
        [
            new(ProbeTransportStatus.Succeeded, TimeSpan.FromMilliseconds(5)),
            new(ProbeTransportStatus.TimedOut, null),
            new(ProbeTransportStatus.Succeeded, TimeSpan.FromMilliseconds(15)),
        ]);
        var result = await new LocalProbeRunner(transport, clock).RunAsync(Execution(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(3, result.AttemptCount);
        Assert.Equal(2, result.SuccessfulAttemptCount);
        Assert.Equal(1m / 3m, result.PacketLossRatio);
        Assert.Equal(5m, result.MinRttMilliseconds);
        Assert.Equal(10m, result.AverageRttMilliseconds);
        Assert.Equal(15m, result.MaxRttMilliseconds);
        Assert.Equal(ProbeErrorCategory.Timeout, result.ErrorCategory);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)], clock.Delays);
        Assert.Equal(3, transport.Requests.Count);
        Assert.Equal(result.StartedAt.AddMilliseconds(500), result.EndedAt);
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public async Task WallClockJumpsDoNotChangeMonotonicCompletion(int wallClockJumpSeconds)
    {
        var clock = new FakeClock();
        var transport = new FakeProbeTransport(
            [new(ProbeTransportStatus.Succeeded, TimeSpan.FromMilliseconds(1)), new(ProbeTransportStatus.TimedOut, null)],
            () =>
            {
                clock.AdvanceMonotonic(TimeSpan.FromMilliseconds(100));
                clock.JumpUtc(TimeSpan.FromSeconds(wallClockJumpSeconds));
            });

        var result = await new LocalProbeRunner(transport, clock).RunAsync(Execution() with { AttemptCount = 2 }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(result.StartedAt.AddMilliseconds(450), result.EndedAt);
        Assert.Equal(ProbeErrorCategory.Timeout, result.ErrorCategory);
    }

    [Fact]
    public async Task ZeroMonotonicElapsedExecutionIsValid()
    {
        var clock = new FakeClock();
        var result = await new LocalProbeRunner(
            new FakeProbeTransport([new(ProbeTransportStatus.Unreachable, null)]),
            clock).RunAsync(Execution() with { AttemptCount = 1 }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(result.StartedAt, result.EndedAt);
        Assert.Equal(ProbeErrorCategory.Unreachable, result.ErrorCategory);
    }

    [Fact]
    public async Task CancellationCreatesNoTargetFailureResult()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeProbeTransport([new(ProbeTransportStatus.Succeeded, TimeSpan.FromMilliseconds(1))]);
        var clock = new FakeClock { CancelDuringDelay = cancellation };

        var result = await new LocalProbeRunner(transport, clock).RunAsync(Execution() with { AttemptCount = 2 }, cancellation.Token);

        Assert.Null(result);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task CancellationDuringAttemptCreatesNoTargetFailureResult()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeProbeTransport(
            [new(ProbeTransportStatus.Succeeded, TimeSpan.FromMilliseconds(1))],
            () =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        var result = await new LocalProbeRunner(transport, new FakeClock()).RunAsync(Execution() with { AttemptCount = 1 }, cancellation.Token);

        Assert.Null(result);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("host.example")]
    [InlineData("::1")]
    [InlineData("999.1.1.1")]
    public void NonIpv4TargetIsRejectedWithoutDns(string target)
    {
        Assert.False(Ipv4ProbeTarget.TryNormalize(target, out _));
    }

    private static LocalProbeExecution Execution() => new(
        7, Guid.Parse("8af9ffdd-551a-41ae-b60b-65e2de7864b5"), "10.0.0.8", 3,
        TimeSpan.FromSeconds(1), LocalProbeExecution.DefaultInterAttemptDelay);

    private sealed class FakeClock : IProbeExecutionClock
    {
        private long timestamp;
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];
        public CancellationTokenSource? CancelDuringDelay { get; init; }
        public DateTimeOffset GetUtcNow() => UtcNow;
        public long GetTimestamp() => timestamp;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            UtcNow += delay;
            timestamp += delay.Ticks;
            CancelDuringDelay?.Cancel();
            return ValueTask.CompletedTask;
        }

        public void AdvanceMonotonic(TimeSpan duration) => timestamp += duration.Ticks;

        public void JumpUtc(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FakeProbeTransport(IEnumerable<ProbeTransportReply> replies, Action? onSend = null) : IProbeTransport
    {
        private readonly Queue<ProbeTransportReply> replies = new(replies);
        public List<ProbeTransportRequest> Requests { get; } = [];
        public ValueTask<ProbeTransportReply> SendAsync(ProbeTransportRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            onSend?.Invoke();
            return ValueTask.FromResult(replies.Dequeue());
        }
    }
}
