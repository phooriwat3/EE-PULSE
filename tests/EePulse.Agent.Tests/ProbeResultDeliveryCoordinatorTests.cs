using System.Net;
using System.Net.Http.Json;
using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Outbox;
using EePulse.Agent.Core.Probing;
using EePulse.Agent.Core.Runtime;
using EePulse.Agent.Core.Transport;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Tests;

public sealed class ProbeResultDeliveryCoordinatorTests
{
    [Fact]
    public async Task ValidPartialAcknowledgementDurablyProcessesOnlyNamedResults()
    {
        var identity = Identity();
        var first = Record(identity.AgentId);
        var second = Record(identity.AgentId);
        var third = Record(identity.AgentId);
        var firstId = first.Envelope.ResultId;
        var secondId = second.Envelope.ResultId;
        var thirdId = third.Envelope.ResultId;
        var outbox = new FakeOutbox(first, second, third);
        var handler = new StubHandler(request =>
        {
            Assert.Equal($"/api/v1/agents/{identity.AgentId:D}/result-batches", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var batch = request.Content!.ReadFromJsonAsync<ProbeResultIngestionBatchRequest>().GetAwaiter().GetResult()!;
            Assert.Equal([firstId, secondId, thirdId], batch.Results.Select(result => result.ResultId));
            return Json(new ProbeResultIngestionBatchResponse(batch.BatchId,
                [firstId],
                [new(secondId, "immutable-fields-invalid")]));
        });
        var coordinator = Coordinator(outbox, handler, 0.5);

        var cycle = await coordinator.DeliverOnceAsync(identity, TestContext.Current.CancellationToken);

        Assert.True(cycle.Delivered);
        Assert.Equal([firstId], outbox.Accepted);
        Assert.Equal(secondId, Assert.Single(outbox.Rejections).ResultId);
        Assert.Equal([thirdId], outbox.Pending.Select(record => record.Envelope.ResultId));
    }

    [Fact]
    public async Task TransportFailureKeepsEveryResultPendingAndUsesDeterministicBoundedFullJitter()
    {
        var identity = Identity();
        var outbox = new FakeOutbox(Record(identity.AgentId));
        var coordinator = Coordinator(outbox, new StubHandler(_ => throw new HttpRequestException()), 0.25);

        var cycle = await coordinator.DeliverOnceAsync(identity, TestContext.Current.CancellationToken);

        Assert.False(cycle.Delivered);
        Assert.True(cycle.HasPendingResults);
        Assert.Equal(TimeSpan.FromMilliseconds(250), cycle.NextDelay);
        Assert.Empty(outbox.Accepted);
        Assert.Empty(outbox.Rejections);
        Assert.Single(outbox.Pending);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthenticationRejectionRecoversPendingCredentialRetainsOutboxAndAllowsLaterAcknowledgement(HttpStatusCode status)
    {
        var identity = Identity(pendingCredential: true);
        var store = new TrackingIdentityStore(identity);
        var outbox = new FakeOutbox(Record(identity.AgentId));
        var handler = new StubHandler(request =>
        {
            if (request.Headers.Authorization!.Parameter == identity.PendingCredential!.Secret)
            {
                return new HttpResponseMessage(status) { Content = JsonContent.Create(new { code = "authentication-rejected" }) };
            }

            var batch = request.Content!.ReadFromJsonAsync<ProbeResultIngestionBatchRequest>().GetAwaiter().GetResult()!;
            return Json(new ProbeResultIngestionBatchResponse(batch.BatchId, [outbox.Records[0].Envelope.ResultId], []));
        });
        var coordinator = Coordinator(outbox, handler, 0.5, store);

        var rejected = await coordinator.DeliverOnceAsync(identity, TestContext.Current.CancellationToken);

        Assert.False(rejected.Delivered);
        Assert.Single(outbox.Pending);
        Assert.Empty(outbox.Accepted);
        Assert.Empty(outbox.Rejections);
        Assert.Null(store.Value!.PendingCredential);
        Assert.Equal(1, store.SaveCount);

        var acknowledged = await coordinator.DeliverOnceAsync(store.Value!, TestContext.Current.CancellationToken);

        Assert.True(acknowledged.Delivered);
        Assert.Empty(outbox.Pending);
        Assert.Single(outbox.Accepted);
    }

    [Fact]
    public async Task ConcurrentAuthenticationRejectionsPerformOnePendingCredentialRecovery()
    {
        var identity = Identity(pendingCredential: true);
        var store = new TrackingIdentityStore(identity);
        var outbox = new FakeOutbox(Record(identity.AgentId));
        var coordinator = Coordinator(outbox, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { code = "authentication-rejected" }) }), 0.5, store);

        await Task.WhenAll(
            coordinator.DeliverOnceAsync(identity, TestContext.Current.CancellationToken).AsTask(),
            coordinator.DeliverOnceAsync(identity, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, store.SaveCount);
        Assert.Single(outbox.Pending);
        Assert.Empty(outbox.Accepted);
    }

    [Fact]
    public async Task AuthenticationRecoveryFailureLeavesOutboxPending()
    {
        var identity = Identity(pendingCredential: true);
        var store = new TrackingIdentityStore(identity) { ThrowOnSave = true };
        var outbox = new FakeOutbox(Record(identity.AgentId));
        var coordinator = Coordinator(outbox, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { code = "authentication-rejected" }) }), 0.5, store);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.DeliverOnceAsync(identity, TestContext.Current.CancellationToken));

        Assert.Single(outbox.Pending);
        Assert.Empty(outbox.Accepted);
        Assert.Empty(outbox.Rejections);
    }

    private static ProbeResultDeliveryCoordinator Coordinator(FakeOutbox outbox, HttpMessageHandler handler, double random, IAgentIdentityStore? store = null) => new(
        outbox,
        new AgentApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://agent.test/") }, store ?? new MemoryIdentityStore(), new NullRevocationHandler(), new NoDelay(), new(new Uri("https://agent.test/"), true)),
        new FixedTimeProvider(),
        new FixedRandom(random));

    private static AgentIdentity Identity(bool pendingCredential = false) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "agent", "test", [],
        new(Guid.NewGuid(), "active-credential-not-logged", DateTimeOffset.MaxValue, DateTimeOffset.MaxValue),
        pendingCredential ? new(Guid.NewGuid(), "pending-credential-not-logged", DateTimeOffset.MaxValue, DateTimeOffset.MaxValue) : null, 20, 60, 1);

    private static ProbeResultOutboxRecord Record(Guid agentId) => new(0, ProbeResultEnvelope.Create(agentId, new LocalProbeResult(
        1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), 1, 1, 0, 1, 1, 1, null)),
        ProbeResultOutboxState.Pending, DateTimeOffset.UnixEpoch, null, null, null, 100);

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class FakeOutbox(params ProbeResultOutboxRecord[] records) : IProbeResultOutbox
    {
        public List<ProbeResultOutboxRecord> Records { get; private set; } = records.Select((record, index) => record with { Sequence = index + 1 }).ToList();
        public IReadOnlyList<ProbeResultOutboxRecord> Pending => Records.Where(record => record.State == ProbeResultOutboxState.Pending).ToArray();
        public List<Guid> Accepted { get; } = [];
        public List<ProbeResultPermanentRejection> Rejections { get; } = [];
        public ValueTask<ProbeResultOutboxRecord> EnqueueAsync(ProbeResultEnvelope envelope, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ProbeResultOutboxRecord>> ReadPendingAsync(ProbeResultOutboxReadLimit limit, CancellationToken cancellationToken) => new(Pending.Take(limit.MaximumCount).ToArray());
        public ValueTask AcknowledgeAsync(IReadOnlyCollection<Guid> resultIds, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ApplyDeliveryOutcomeAsync(IReadOnlyCollection<Guid> accepted, IReadOnlyCollection<ProbeResultPermanentRejection> rejected, DateTimeOffset processedAt, CancellationToken cancellationToken)
        {
            Accepted.AddRange(accepted); Rejections.AddRange(rejected);
            Records = Records.Select(record => accepted.Contains(record.Envelope.ResultId) ? record with { State = ProbeResultOutboxState.Acknowledged } : record).Where(record => !rejected.Any(item => item.ResultId == record.Envelope.ResultId)).ToList();
            return ValueTask.CompletedTask;
        }
        public ValueTask<int> CleanupAcknowledgedAsync(DateTimeOffset cleanupThrough, int maximumCount, CancellationToken cancellationToken) => new(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private sealed class MemoryIdentityStore : IAgentIdentityStore
    {
        public ValueTask<AgentIdentity?> LoadAsync(CancellationToken cancellationToken) => new((AgentIdentity?)null);
        public ValueTask SaveAsync(AgentIdentity identity, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class TrackingIdentityStore(AgentIdentity identity) : IAgentIdentityStore
    {
        private readonly object gate = new();
        public AgentIdentity? Value { get; private set; } = identity;
        public int SaveCount { get; private set; }
        public bool ThrowOnSave { get; init; }
        public ValueTask<AgentIdentity?> LoadAsync(CancellationToken cancellationToken) { lock (gate) return new(Value); }
        public ValueTask SaveAsync(AgentIdentity value, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (ThrowOnSave) throw new InvalidOperationException("identity-store-failure");
                Value = value; SaveCount++;
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteAsync(CancellationToken cancellationToken) { lock (gate) Value = null; return ValueTask.CompletedTask; }
    }
    private sealed class NullRevocationHandler : IAgentRevocationHandler { public ValueTask HandleRevocationAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask; }
    private sealed class NoDelay : IAgentRetryDelay { public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => ValueTask.CompletedTask; }
    private sealed class FixedRandom(double value) : IProbeResultDeliveryRandom { public double NextDouble() => value; }
    private sealed class FixedTimeProvider : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(1); }
}
