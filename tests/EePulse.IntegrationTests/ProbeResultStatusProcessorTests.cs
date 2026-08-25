using EePulse.Application.Time;
using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace EePulse.IntegrationTests;

public sealed class ProbeResultStatusProcessorTests
{
    [Fact]
    public async Task ProcessesKernelOutcomesAndAdvancesStateVersionForEveryAppliedResult()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-4), fixture.Now, successes: 3, packetLossRatio: 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-3), fixture.Now.AddSeconds(1), successes: 3, packetLossRatio: 0m, averageRtt: 500m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-2), fixture.Now.AddSeconds(2), successes: 0, packetLossRatio: 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-1), fixture.Now.AddSeconds(3), successes: 0, packetLossRatio: 1m);
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now.AddSeconds(4), successes: 3, packetLossRatio: 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(5), successes: 3, packetLossRatio: 0m);

        await using var db = new EePulseDbContext(fixture.Options);
        var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now.AddMinutes(1)));
        for (var index = 0; index < 6; index++) Assert.Equal(ProbeResultStatusProcessorOutcomeKind.Processed, (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);

        await using var verify = new EePulseDbContext(fixture.Options);
        var projection = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(ProbeStatus.Up, projection.UnderlyingStatus);
        Assert.Equal(0, projection.ConsecutiveFailureCount);
        Assert.Equal(2, projection.ConsecutiveSuccessCount);
        Assert.Equal(6, projection.StateVersion);
        Assert.Equal(6, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UsesDeterministicCursorOrderIncludingEqualEndedAtAndCommittedReplayIsIdempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        var eventAt = fixture.Now;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[2]);
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[0]);
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[1]);

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now));
            var processedIds = new List<Guid>();
            for (var index = 0; index < 3; index++)
                processedIds.Add((await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId!.Value);
            Assert.Equal(ids, processedIds);
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(3, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentSameProbeCallsDoNotDuplicateApplication()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);

        await using var first = new EePulseDbContext(fixture.Options);
        await using var second = new EePulseDbContext(fixture.Options);
        await Task.WhenAll(
            new ProbeResultStatusProcessor(first, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken),
            new ProbeResultStatusProcessor(second, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(2, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).StateVersion);
    }

    [Fact]
    public async Task PreCommitFailureRollsBackProjectionAndDispositionThenRetrySucceeds()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var interceptor = new ThrowBeforeSaveInterceptor();
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(interceptor).Options;

        await using (var failing = new EePulseDbContext(failingOptions))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        }

        await using (var afterFailure = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await afterFailure.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var retry = new EePulseDbContext(fixture.Options);
        Assert.Equal(ProbeResultStatusProcessorOutcomeKind.Processed, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesTheLedgerRowPendingForRetry()
    {
        await using var fixture = await CreateFixtureAsync();
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var blocker = new BlockingSaveInterceptor();
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(blocker).Options;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await using (var interrupted = new EePulseDbContext(options))
        {
            var processing = new ProbeResultStatusProcessor(interrupted, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, cancellation.Token);
            await blocker.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        }

        await using var retry = new EePulseDbContext(fixture.Options);
        Assert.Equal(resultId, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
    }

    [Fact]
    public async Task IngestionTransactionWaitsForProcessorLockAndPostReleaseRowIsProcessedNext()
    {
        await using var fixture = await CreateFixtureAsync();
        var firstResultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var blocker = new BlockingSaveInterceptor();
        var processorOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(blocker).Options;
        await using var processorDb = new EePulseDbContext(processorOptions);
        var processing = new ProbeResultStatusProcessor(processorDb, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await blocker.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await using var ingestionDb = new EePulseDbContext(fixture.Options);
        await using var ingestionTransaction = await ingestionDb.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var ingestionBackendProcessId = await ingestionDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(TestContext.Current.CancellationToken);
        var ingestionLock = ProbeTransactionLock.AcquireAsync(ingestionDb, fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var observer = new NpgsqlConnection(fixture.ConnectionString))
        {
            await observer.OpenAsync(TestContext.Current.CancellationToken);
            await WaitForUngrantedProbeLockAsync(observer, ingestionBackendProcessId, fixture.ProbeId, ingestionLock, TestContext.Current.CancellationToken);
        }

        blocker.Release.TrySetResult();
        Assert.Equal(firstResultId, (await processing).ResultId);
        await ingestionLock;
        var secondResultId = Guid.NewGuid();
        ingestionDb.Add(CreateLedger(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m, 1, secondResultId, 1m));
        await ingestionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        await ingestionTransaction.CommitAsync(TestContext.Current.CancellationToken);

        await using (var verify = new EePulseDbContext(fixture.Options))
            Assert.Equal(1, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        await using var nextDb = new EePulseDbContext(fixture.Options);
        Assert.Equal(secondResultId, (await new ProbeResultStatusProcessor(nextDb, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
    }

    [Fact]
    public async Task ResolvesExactSnapshotAndEffectiveBoundaryUsingPersistedPostgresTimestamps()
    {
        await using var fixture = await CreateFixtureAsync();
        var second = await AddConfigurationVersionAsync(fixture, 2, failureThreshold: 1, recoveryThreshold: 1);
        var boundary = await ReadBoundaryAsync(fixture, fixture.AgentId, 2);
        var effectiveBoundaryResultId = await AddLedgerAsync(fixture, boundary, boundary, 3, 0m, configurationVersion: 2);
        var failureResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(1)), boundary.Add(TimeSpan.FromMicroseconds(1)), 0, 1m, configurationVersion: 2);
        var recoveryResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(2)), boundary.Add(TimeSpan.FromMicroseconds(2)), 3, 0m, configurationVersion: 2);
        var earlierBoundaryResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(-1)), boundary.Add(TimeSpan.FromMicroseconds(-1)), 3, 0m, configurationVersion: 2);
        var persistedBoundary = await ReadBoundaryAsync(fixture, fixture.AgentId, 2);
        var effectiveBoundaryLedger = await ReadLedgerAsync(fixture, effectiveBoundaryResultId);
        var failureLedger = await ReadLedgerAsync(fixture, failureResultId);
        var recoveryLedger = await ReadLedgerAsync(fixture, recoveryResultId);
        var earlierBoundaryLedger = await ReadLedgerAsync(fixture, earlierBoundaryResultId);

        Assert.Equal(persistedBoundary, effectiveBoundaryLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(-1)), earlierBoundaryLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(1)), failureLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(2)), recoveryLedger.ReceivedAt);

        await using var db = new EePulseDbContext(fixture.Options);
        var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now));
        for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var dispositions = await verify.ProbeResultProcessingDispositions.OrderBy(row => row.EventAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(dispositions, row => row.ResultId == earlierBoundaryResultId && row.Disposition == ProbeResultProcessingDispositionKind.HistoricalOther && row.ReasonCode == "config-not-effective");
        Assert.Contains(dispositions, row => row.ResultId == effectiveBoundaryResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.All(dispositions.Where(row => row.ResolvedPolicySnapshotId.HasValue), row => Assert.Equal(second.PolicyId, row.ResolvedPolicySnapshotId));
        Assert.Equal(ProbeStatus.Up, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).UnderlyingStatus);
    }

    [Fact]
    public async Task HistoricalOnlyLineageAndCursorAndTimeOutcomesDoNotCreateOrMutateProjection()
    {
        await using var fixture = await CreateFixtureAsync(includeBinding: false);
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var db = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var verify = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await verify.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("policy-lineage-unresolved", disposition.ReasonCode);
        }

        await using var timed = await CreateFixtureAsync();
        var boundary = await ReadBoundaryAsync(timed, timed.AgentId, 1);
        var latenessEqualityResultId = await AddLedgerAsync(timed, boundary.AddMinutes(-5), boundary, 3, 0m);
        var beyondLatenessResultId = await AddLedgerAsync(timed, boundary.AddMinutes(-5).Add(TimeSpan.FromMicroseconds(-1)), boundary, 3, 0m);
        var futureSkewEqualityResultId = await AddLedgerAsync(timed, boundary.AddSeconds(60), boundary, 3, 0m);
        var futureSkewResultId = await AddLedgerAsync(timed, boundary.AddSeconds(60).Add(TimeSpan.FromMicroseconds(1)), boundary, 3, 0m);
        var persistedBoundary = await ReadBoundaryAsync(timed, timed.AgentId, 1);
        var latenessEqualityLedger = await ReadLedgerAsync(timed, latenessEqualityResultId);
        var beyondLatenessLedger = await ReadLedgerAsync(timed, beyondLatenessResultId);
        var futureSkewEqualityLedger = await ReadLedgerAsync(timed, futureSkewEqualityResultId);
        var futureSkewLedger = await ReadLedgerAsync(timed, futureSkewResultId);

        Assert.Equal(persistedBoundary, latenessEqualityLedger.ReceivedAt);
        Assert.Equal(latenessEqualityLedger.ReceivedAt.AddMinutes(-5), latenessEqualityLedger.EndedAt);
        Assert.Equal(beyondLatenessLedger.ReceivedAt.AddMinutes(-5).Add(TimeSpan.FromMicroseconds(-1)), beyondLatenessLedger.EndedAt);
        Assert.Equal(futureSkewEqualityLedger.ReceivedAt.AddSeconds(60), futureSkewEqualityLedger.EndedAt);
        Assert.Equal(futureSkewLedger.ReceivedAt.AddSeconds(60).Add(TimeSpan.FromMicroseconds(1)), futureSkewLedger.EndedAt);
        await using (var db = new EePulseDbContext(timed.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(timed.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(timed.ProbeId, TestContext.Current.CancellationToken);
        }
        await using var timeVerify = new EePulseDbContext(timed.Options);
        Assert.Equal(2, (await timeVerify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).StateVersion);
        var timedDispositions = await timeVerify.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(timedDispositions, row => row.ResultId == latenessEqualityResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.Contains(timedDispositions, row => row.ResultId == beyondLatenessResultId && row.ReasonCode == "beyond-approved-lateness");
        Assert.Contains(timedDispositions, row => row.ResultId == futureSkewEqualityResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.Contains(timedDispositions, row => row.ResultId == futureSkewResultId && row.ReasonCode == "future-or-skew-suspect");

        await using var cursor = await CreateFixtureAsync();
        await AddLedgerAsync(cursor, cursor.Now, cursor.Now, 3, 0m);
        await using (var db = new EePulseDbContext(cursor.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(cursor.Now)).ProcessNextAsync(cursor.ProbeId, TestContext.Current.CancellationToken);
        await AddLedgerAsync(cursor, cursor.Now.AddSeconds(-1), cursor.Now, 3, 0m);
        await using (var db = new EePulseDbContext(cursor.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(cursor.Now)).ProcessNextAsync(cursor.ProbeId, TestContext.Current.CancellationToken);
        await using (var cursorVerify = new EePulseDbContext(cursor.Options))
            Assert.Contains(await cursorVerify.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken), row => row.ReasonCode == "late-order");

        await using var noBoundary = await CreateFixtureAsync(includeBoundary: false);
        await AddLedgerAsync(noBoundary, noBoundary.Now, noBoundary.Now, 3, 0m);
        await using (var db = new EePulseDbContext(noBoundary.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(noBoundary.Now)).ProcessNextAsync(noBoundary.ProbeId, TestContext.Current.CancellationToken);
        await using (var boundaryVerify = new EePulseDbContext(noBoundary.Options))
        {
            Assert.Empty(await boundaryVerify.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal("policy-lineage-unresolved", (await boundaryVerify.ProbeResultProcessingDispositions.SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(bool includeBinding = true, bool includeBoundary = true)
    {
        var postgres = await PostgresTestDatabase.StartAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migration = new EePulseDbContext(options)) await migration.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var site = new Site(Guid.NewGuid(), "P" + Guid.NewGuid().ToString("N")[..5], "Processor", "UTC", now);
        var group = new AgentGroup(Guid.NewGuid(), "Processor", null, now);
        var device = new Device(Guid.NewGuid(), site.Id, "Processor", "192.0.2.70", null, "Server", null, null, Criticality.Normal, [], now);
        var probe = new Probe(Guid.NewGuid(), device.Id, group.Id, 30, 2000, 3, 500, null, 2, 2);
        var agent = new EePulse.Domain.Agents.Agent(Guid.NewGuid(), group.Id, Guid.NewGuid(), "processor", "1.0.0", 20, now);
        var configuration = new AgentConfigurationSnapshot(group.Id, 1, "{}", new byte[32], now, null);
        var acknowledgement = new AgentConfigurationAcknowledgement(Guid.NewGuid(), agent.Id, 1, AgentAcknowledgementStatus.Applied, now, now, now, null, 1, 1);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 2, 2, 500, null, now);
        var boundary = new AgentConfigurationEffectiveBoundary(agent.Id, 1, acknowledgement.Id, AgentAcknowledgementStatus.Applied, acknowledgement.ReceivedAt);
        await using (var db = new EePulseDbContext(options))
        {
            db.AddRange(site, group, device, probe, agent, configuration, acknowledgement, policy);
            if (includeBoundary) db.Add(boundary);
            if (includeBinding) db.Add(new ProbeStatusPolicyBinding(probe.Id, 1, group.Id, policy.Id));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        return new(postgres, postgres.ConnectionString, options, now, group.Id, probe.Id, agent.Id, policy.Id);
    }

    private static async Task<(Guid PolicyId, DateTimeOffset Boundary)> AddConfigurationVersionAsync(Fixture fixture, long version, int failureThreshold, int recoveryThreshold)
    {
        var at = fixture.Now.AddMinutes(1);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), (int)version, failureThreshold, recoveryThreshold, null, null, at);
        await using var db = new EePulseDbContext(fixture.Options);
        var configuration = new AgentConfigurationSnapshot(fixture.GroupId, version, "{}", new byte[32], at, null);
        var acknowledgement = new AgentConfigurationAcknowledgement(Guid.NewGuid(), fixture.AgentId, version, AgentAcknowledgementStatus.Applied, at, at, at, null, version, version);
        db.AddRange(configuration, acknowledgement, policy,
            new AgentConfigurationEffectiveBoundary(fixture.AgentId, version, acknowledgement.Id, AgentAcknowledgementStatus.Applied, acknowledgement.ReceivedAt),
            new ProbeStatusPolicyBinding(fixture.ProbeId, version, fixture.GroupId, policy.Id));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (policy.Id, acknowledgement.ReceivedAt);
    }

    private static async Task<DateTimeOffset> ReadBoundaryAsync(Fixture fixture, Guid agentId, long version)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.AgentConfigurationEffectiveBoundaries.Where(row => row.AgentId == agentId && row.ConfigurationVersion == version)
            .Select(row => row.AppliedAcknowledgementReceivedAt).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ProbeResultLedgerEntry> ReadLedgerAsync(Fixture fixture, Guid resultId)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.ProbeResultLedgerEntries.AsNoTracking()
            .SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> AddLedgerAsync(Fixture fixture, DateTimeOffset endedAt, DateTimeOffset receivedAt, int successes, decimal packetLossRatio, long configurationVersion = 1, Guid? resultId = null, decimal? averageRtt = 1m)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var id = resultId ?? Guid.NewGuid();
        db.Add(CreateLedger(fixture, endedAt, receivedAt, successes, packetLossRatio, configurationVersion, id, averageRtt));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static ProbeResultLedgerEntry CreateLedger(Fixture fixture, DateTimeOffset endedAt, DateTimeOffset receivedAt, int successes, decimal packetLossRatio, long configurationVersion, Guid resultId, decimal? averageRtt) =>
        new(fixture.AgentId, resultId, fixture.ProbeId, configurationVersion,
            endedAt.AddSeconds(-1), endedAt, 3, successes, packetLossRatio, averageRtt, averageRtt, averageRtt, null, new byte[32], receivedAt);

    private static async Task WaitForUngrantedProbeLockAsync(NpgsqlConnection observer, int backendProcessId, Guid probeId, Task acquiring, CancellationToken cancellationToken)
    {
        var canonicalProbeId = probeId.ToString("D");
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND pid = @backendProcessId AND NOT granted
                      AND (lpad(to_hex(classid::bigint), 8, '0') || lpad(to_hex(objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0'))
                """, observer);
            command.Parameters.AddWithValue("backendProcessId", backendProcessId);
            command.Parameters.AddWithValue("probeId", canonicalProbeId);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return;
            if (acquiring.IsCompleted)
            {
                await acquiring;
                throw new Xunit.Sdk.XunitException("The ingestion transaction acquired the Probe lock before the processor released it.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private sealed record Fixture(IAsyncDisposable Postgres, string ConnectionString, DbContextOptions<EePulseDbContext> Options, DateTimeOffset Now, Guid GroupId, Guid ProbeId, Guid AgentId, Guid PolicyId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Postgres.DisposeAsync();
    }
    private sealed class FixedClock(DateTimeOffset now) : IUtcClock { public DateTimeOffset UtcNow => now; }
    private sealed class ThrowBeforeSaveInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) => throw new InvalidOperationException("test pre-commit failure");
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("test pre-commit failure"));
    }
    private sealed class BlockingSaveInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
