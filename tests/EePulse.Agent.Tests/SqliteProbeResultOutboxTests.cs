using EePulse.Agent.Core.Outbox;
using EePulse.Agent.Core.Probing;
using EePulse.Agent.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace EePulse.Agent.Tests;

public sealed class SqliteProbeResultOutboxTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"ee-pulse-outbox-{Guid.NewGuid():N}");
    private readonly Guid agentId = Guid.NewGuid();

    [Fact]
    public async Task EnqueuePersistsCompleteImmutableEnvelopeAcrossReopen()
    {
        var path = DatabasePath();
        var envelope = Envelope();
        await using (var outbox = new SqliteProbeResultOutbox(path))
        {
            var stored = await outbox.EnqueueAsync(envelope, TestContext.Current.CancellationToken);
            Assert.Equal(ProbeResultOutboxState.Pending, stored.State);
            Assert.Equal(envelope, stored.Envelope);
        }

        await using var reopened = new SqliteProbeResultOutbox(path);
        var pending = await reopened.ReadPendingAsync(new(10, 1_000_000), TestContext.Current.CancellationToken);
        var record = Assert.Single(pending);
        Assert.Equal(envelope, record.Envelope);
        Assert.Equal(1, record.Sequence);
        Assert.Empty(Directory.EnumerateDirectories(directory, "outbox.db.quarantine-*"));
    }

    [Fact]
    public async Task DuplicateImmutableResultIdIsRejected()
    {
        await using var outbox = new SqliteProbeResultOutbox(DatabasePath());
        var envelope = Envelope();
        await outbox.EnqueueAsync(envelope, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await outbox.EnqueueAsync(envelope, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PendingReadsAreFifoAndBoundedByCountAndBytes()
    {
        await using var outbox = new SqliteProbeResultOutbox(DatabasePath());
        var first = await outbox.EnqueueAsync(Envelope(), TestContext.Current.CancellationToken);
        var second = await outbox.EnqueueAsync(Envelope(), TestContext.Current.CancellationToken);
        var third = await outbox.EnqueueAsync(Envelope(), TestContext.Current.CancellationToken);

        var countBounded = await outbox.ReadPendingAsync(new(2, 1_000_000), TestContext.Current.CancellationToken);
        Assert.Equal([first.Envelope.ResultId, second.Envelope.ResultId], countBounded.Select(record => record.Envelope.ResultId));

        var byteBounded = await outbox.ReadPendingAsync(
            new(3, first.SerializedByteCount + second.SerializedByteCount), TestContext.Current.CancellationToken);
        Assert.Equal([first.Envelope.ResultId, second.Envelope.ResultId], byteBounded.Select(record => record.Envelope.ResultId));

        var tooSmallForFirst = await outbox.ReadPendingAsync(new(3, first.SerializedByteCount - 1), TestContext.Current.CancellationToken);
        Assert.Empty(tooSmallForFirst);
        Assert.Equal(3, third.Sequence);
    }

    [Fact]
    public async Task AcknowledgementsAreDurableAndOnlyAcknowledgedRecordsAreCleanupEligible()
    {
        var path = DatabasePath();
        var acknowledgedAt = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        ProbeResultOutboxRecord first;
        ProbeResultOutboxRecord second;
        await using (var outbox = new SqliteProbeResultOutbox(path))
        {
            first = await outbox.EnqueueAsync(Envelope(), TestContext.Current.CancellationToken);
            second = await outbox.EnqueueAsync(Envelope(), TestContext.Current.CancellationToken);
            await outbox.AcknowledgeAsync([first.Envelope.ResultId], acknowledgedAt, TestContext.Current.CancellationToken);
            await outbox.AcknowledgeAsync([first.Envelope.ResultId], acknowledgedAt.AddMinutes(1), TestContext.Current.CancellationToken);
            Assert.Equal(0, await outbox.CleanupAcknowledgedAsync(acknowledgedAt.AddTicks(-1), 10, TestContext.Current.CancellationToken));
        }

        await using var reopened = new SqliteProbeResultOutbox(path);
        var pending = await reopened.ReadPendingAsync(new(10, 1_000_000), TestContext.Current.CancellationToken);
        Assert.Equal([second.Envelope.ResultId], pending.Select(record => record.Envelope.ResultId));
        Assert.Equal(1, await reopened.CleanupAcknowledgedAsync(acknowledgedAt, 10, TestContext.Current.CancellationToken));
        var afterCleanup = await reopened.ReadPendingAsync(new(10, 1_000_000), TestContext.Current.CancellationToken);
        Assert.Equal([second.Envelope.ResultId], afterCleanup.Select(record => record.Envelope.ResultId));
    }

    [Fact]
    public async Task OperationsHonorCancellation()
    {
        await using var outbox = new SqliteProbeResultOutbox(DatabasePath());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await outbox.EnqueueAsync(Envelope(), cancellation.Token));
    }

    [Fact]
    public async Task CorruptExistingDatabaseFailsClosedWithoutReplacement()
    {
        var path = DatabasePath();
        Directory.CreateDirectory(directory);
        var corruptBytes = new byte[] { 1, 2, 3, 4 };
        var walBytes = new byte[] { 5, 6 };
        var shmBytes = new byte[] { 7, 8 };
        await File.WriteAllBytesAsync(path, corruptBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(path + "-wal", walBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(path + "-shm", shmBytes, TestContext.Current.CancellationToken);

        await using var outbox = new SqliteProbeResultOutbox(path, TestDiskCapacityProvider);
        var exception = await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await outbox.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));

        Assert.Equal(OutboxRecoveryStatus.CorruptQuarantined, exception.Recovery.Status);
        var evidencePath = Assert.IsType<string>(exception.Recovery.EvidencePath);
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(Path.Combine(evidencePath, "outbox.db"), TestContext.Current.CancellationToken));
        Assert.Equal(walBytes, await File.ReadAllBytesAsync(Path.Combine(evidencePath, "outbox.db-wal"), TestContext.Current.CancellationToken));
        Assert.Equal(shmBytes, await File.ReadAllBytesAsync(Path.Combine(evidencePath, "outbox.db-shm"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".recovery-required"));
        await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await outbox.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));
        await using var restarted = new SqliteProbeResultOutbox(path, TestDiskCapacityProvider);
        var restartException = await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await restarted.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));
        Assert.Equal(evidencePath, restartException.Recovery.EvidencePath);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SnapshotAdmissionAtReserveFailsClosedWithoutOpeningOrMutatingEvidence()
    {
        var path = DatabasePath();
        Directory.CreateDirectory(directory);
        var databaseBytes = new byte[] { 1, 2, 3, 4 };
        var walBytes = new byte[] { 5, 6 };
        var shmBytes = new byte[] { 7, 8 };
        await File.WriteAllBytesAsync(path, databaseBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(path + "-wal", walBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(path + "-shm", shmBytes, TestContext.Current.CancellationToken);
        var reserve = OutboxStoragePressure.MinimumReserveBytes;
        await using var outbox = new SqliteProbeResultOutbox(
            path,
            new FixedDiskCapacityProvider(new(10L * 1024 * 1024 * 1024, reserve + 7)));

        var exception = await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await outbox.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));

        Assert.Equal(OutboxRecoveryStatus.SnapshotNotAdmitted, exception.Recovery.Status);
        Assert.Equal(databaseBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(walBytes, await File.ReadAllBytesAsync(path + "-wal", TestContext.Current.CancellationToken));
        Assert.Equal(shmBytes, await File.ReadAllBytesAsync(path + "-shm", TestContext.Current.CancellationToken));
        Assert.True(File.Exists(path + ".recovery-required"));
    }

    [Fact]
    public async Task MarkerWriteFailureLeavesAResidualSnapshotThatBlocksRestart()
    {
        var path = DatabasePath();
        Directory.CreateDirectory(directory);
        var corruptBytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(path, corruptBytes, TestContext.Current.CancellationToken);
        await using (var outbox = new SqliteProbeResultOutbox(
            path,
            new FixedDiskCapacityProvider(new(30L * 1024 * 1024 * 1024, 20L * 1024 * 1024 * 1024)),
            new FailingMarkerStore()))
        {
            var exception = await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
                await outbox.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));
            Assert.Equal(OutboxRecoveryStatus.RecoveryMarkerFailed, exception.Recovery.Status);
        }

        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateDirectories(directory, "outbox.db.quarantine-*"));
        await using var restarted = new SqliteProbeResultOutbox(path);
        var restartException = await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await restarted.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));
        Assert.Equal(OutboxRecoveryStatus.SnapshotNotAdmitted, restartException.Recovery.Status);
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EvidenceSizeArithmeticFailsOnOverflow()
    {
        Assert.Throws<OverflowException>(() =>
            OutboxEvidenceSnapshotAdmission.CalculateEvidenceBytes([long.MaxValue, 1]));
    }

    [Fact]
    public async Task SnapshotAdmissionClaimAllowsOnlyOneInitializerAndHealthyCompletionReleasesIt()
    {
        var path = DatabasePath();
        await using (var seed = new SqliteProbeResultOutbox(path, TestDiskCapacityProvider))
        {
            await seed.EnqueueAsync(Envelope(), TestContext.Current.CancellationToken);
        }

        var originalDatabaseBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var gate = new BlockingDiskCapacityProvider(new(30L * 1024 * 1024 * 1024, 20L * 1024 * 1024 * 1024));
        await using var winner = new SqliteProbeResultOutbox(path, gate);
        var winnerTask = winner.ReadPendingAsync(new(10, 1_000_000), TestContext.Current.CancellationToken).AsTask();
        await gate.WaitUntilEnteredAsync(TestContext.Current.CancellationToken);

        await using var loser = new SqliteProbeResultOutbox(path, TestDiskCapacityProvider);
        var loserException = await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await loser.ReadPendingAsync(new(10, 1_000_000), TestContext.Current.CancellationToken));
        Assert.Equal(OutboxRecoveryStatus.SnapshotAdmissionClaimed, loserException.Recovery.Status);
        Assert.Empty(Directory.EnumerateDirectories(directory, "outbox.db.quarantine-*"));
        Assert.Equal(originalDatabaseBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        gate.Release();
        Assert.Single(await winnerTask);
        Assert.False(File.Exists(path + ".snapshot-admission"));
        Assert.Empty(Directory.EnumerateDirectories(directory, "outbox.db.quarantine-*"));

        await using var later = new SqliteProbeResultOutbox(path, TestDiskCapacityProvider);
        Assert.Single(await later.ReadPendingAsync(new(10, 1_000_000), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StaleSnapshotAdmissionClaimBlocksRestartWithoutOpeningTheOutbox()
    {
        var path = DatabasePath();
        await using (var seed = new SqliteProbeResultOutbox(path, TestDiskCapacityProvider))
        {
            await seed.EnqueueAsync(Envelope(), TestContext.Current.CancellationToken);
        }

        var originalBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path + ".snapshot-admission", "ee-pulse-outbox-snapshot-admission-v1\n", TestContext.Current.CancellationToken);
        await using var restarted = new SqliteProbeResultOutbox(path, TestDiskCapacityProvider);

        var exception = await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await restarted.ReadPendingAsync(new(10, 1_000_000), TestContext.Current.CancellationToken));
        Assert.Equal(OutboxRecoveryStatus.SnapshotAdmissionClaimed, exception.Recovery.Status);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SnapshotAdmissionClaimCleanupFailureRemainsFailClosed()
    {
        var path = DatabasePath();
        await using var outbox = new SqliteProbeResultOutbox(
            path,
            TestDiskCapacityProvider,
            snapshotAdmissionClaimStore: new FailingCompletionClaimStore());

        var exception = await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await outbox.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));
        Assert.Equal(OutboxRecoveryStatus.SnapshotCleanupFailed, exception.Recovery.Status);
        Assert.True(File.Exists(path + ".snapshot-admission"));
        Assert.True(File.Exists(path + ".recovery-required"));

        await using var restarted = new SqliteProbeResultOutbox(path, TestDiskCapacityProvider);
        await Assert.ThrowsAsync<OutboxCorruptionException>(async () =>
            await restarted.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnsupportedStoredSchemaFailsClosedWithoutMutatingRecords()
    {
        var path = DatabasePath();
        var envelope = Envelope();
        await using (var outbox = new SqliteProbeResultOutbox(path))
        {
            await outbox.EnqueueAsync(envelope, TestContext.Current.CancellationToken);
        }

        await using (var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE probe_result_outbox SET result_schema_version = 2;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var reopened = new SqliteProbeResultOutbox(path);
        var exception = await Assert.ThrowsAsync<OutboxUnsupportedSchemaException>(async () =>
            await reopened.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken));
        Assert.Equal(2, exception.ResultSchemaVersion);
        await Assert.ThrowsAsync<OutboxUnsupportedSchemaException>(async () =>
            await reopened.AcknowledgeAsync(
                [envelope.ResultId], new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<OutboxUnsupportedSchemaException>(async () =>
            await reopened.CleanupAcknowledgedAsync(
                new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero), 1, TestContext.Current.CancellationToken));

        await using var verification = new SqliteConnection($"Data Source={path};Mode=ReadWrite");
        await verification.OpenAsync(TestContext.Current.CancellationToken);
        await using var verify = verification.CreateCommand();
        verify.CommandText = "SELECT result_schema_version, state FROM probe_result_outbox WHERE result_id = $resultId;";
        verify.Parameters.AddWithValue("$resultId", envelope.ResultId.ToString("D"));
        await using var reader = await verify.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
    }

    [Fact]
    public async Task ConcurrentEnqueuesRemainCompleteAndUnique()
    {
        var path = DatabasePath();
        await using var firstOutbox = new SqliteProbeResultOutbox(path);
        await firstOutbox.ReadPendingAsync(new(1, 1_000), TestContext.Current.CancellationToken);
        await using var secondOutbox = new SqliteProbeResultOutbox(path);
        var envelopes = Enumerable.Range(0, 16).Select(_ => Envelope()).ToArray();
        var records = await Task.WhenAll(envelopes.Select((envelope, index) =>
            (index % 2 == 0 ? firstOutbox : secondOutbox).EnqueueAsync(envelope, TestContext.Current.CancellationToken).AsTask()));

        Assert.Equal(16, records.Length);
        Assert.Equal(16, records.Select(record => record.Envelope.ResultId).Distinct().Count());
        var pending = await firstOutbox.ReadPendingAsync(new(16, 1_000_000), TestContext.Current.CancellationToken);
        Assert.Equal(records.Select(record => record.Envelope.ResultId).OrderBy(id => id), pending.Select(record => record.Envelope.ResultId).OrderBy(id => id));
        Assert.Equal(Enumerable.Range(1, 16).Select(value => (long)value), pending.Select(record => record.Sequence));
    }

    [Fact]
    public async Task ConcurrentEnqueueAndAcknowledgementPreserveDurableStates()
    {
        var path = DatabasePath();
        var acknowledged = Envelope();
        var additional = Enumerable.Range(0, 8).Select(_ => Envelope()).ToArray();
        await using (var outbox = new SqliteProbeResultOutbox(path))
        {
            await outbox.EnqueueAsync(acknowledged, TestContext.Current.CancellationToken);
            await using var concurrentOutbox = new SqliteProbeResultOutbox(path);
            var enqueues = additional.Select(envelope => concurrentOutbox.EnqueueAsync(envelope, TestContext.Current.CancellationToken).AsTask());
            await Task.WhenAll(enqueues.Cast<Task>().Append(outbox.AcknowledgeAsync(
                [acknowledged.ResultId], new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken).AsTask()));
        }

        await using var reopened = new SqliteProbeResultOutbox(path);
        var pending = await reopened.ReadPendingAsync(new(16, 1_000_000), TestContext.Current.CancellationToken);
        Assert.Equal(additional.Select(envelope => envelope.ResultId).OrderBy(id => id), pending.Select(record => record.Envelope.ResultId).OrderBy(id => id));
        Assert.DoesNotContain(pending, record => record.Envelope.ResultId == acknowledged.ResultId);
    }

    [Theory]
    [InlineData(79, 100, false, OutboxStoragePressureState.Healthy)]
    [InlineData(80, 100, false, OutboxStoragePressureState.Degraded)]
    [InlineData(95, 100, false, OutboxStoragePressureState.Suspended)]
    [InlineData(75, 100, true, OutboxStoragePressureState.Suspended)]
    [InlineData(69, 100, true, OutboxStoragePressureState.Healthy)]
    public void StoragePressureUsesFrozenQuotaThresholds(
        int usagePercent,
        int quotaPercent,
        bool wasSuspended,
        OutboxStoragePressureState expected)
    {
        const long quota = 100_000;
        var snapshot = OutboxStoragePressure.Calculate(
            quota * usagePercent / quotaPercent,
            hostingVolumeBytes: 100L * 1024 * 1024 * 1024,
            availableVolumeBytes: 50L * 1024 * 1024 * 1024,
            wasSuspended,
            quota);

        Assert.Equal(expected, snapshot.State);
        Assert.Equal(10L * 1024 * 1024 * 1024, snapshot.ReserveBytes);
    }

    [Fact]
    public void ReserveBreachSuspendsRegardlessOfQuotaUsage()
    {
        var snapshot = OutboxStoragePressure.Calculate(
            0,
            hostingVolumeBytes: 100L * 1024 * 1024 * 1024,
            availableVolumeBytes: 1L * 1024 * 1024 * 1024,
            wasSuspended: false,
            quotaBytes: 100_000);

        Assert.True(snapshot.IsReserveBreached);
        Assert.Equal(OutboxStoragePressureState.Suspended, snapshot.State);
    }

    [Fact]
    public void StoragePressureThresholdsAreSafeAtLongMaximum()
    {
        var quota = long.MaxValue;
        var threshold95 = quota / 100 * 95 + quota % 100 * 95 / 100;
        var threshold70 = quota / 100 * 70 + quota % 100 * 70 / 100;
        var hostingVolume = long.MaxValue;
        var available = long.MaxValue;

        Assert.Equal(OutboxStoragePressureState.Suspended,
            OutboxStoragePressure.Calculate(threshold95, hostingVolume, available, false, quota).State);
        Assert.Equal(OutboxStoragePressureState.Degraded,
            OutboxStoragePressure.Calculate(threshold95 - 1, hostingVolume, available, false, quota).State);
        Assert.Equal(OutboxStoragePressureState.Healthy,
            OutboxStoragePressure.Calculate(threshold70 - 1, hostingVolume, available, true, quota).State);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string DatabasePath() => Path.Combine(directory, "outbox.db");

    private static readonly IOutboxDiskCapacityProvider TestDiskCapacityProvider = new FixedDiskCapacityProvider(
        new(30L * 1024 * 1024 * 1024, 20L * 1024 * 1024 * 1024));

    private ProbeResultEnvelope Envelope() => ProbeResultEnvelope.Create(
        agentId,
        new LocalProbeResult(
            ConfigurationVersion: 7,
            ProbeId: Guid.NewGuid(),
            StartedAt: new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero),
            EndedAt: new DateTimeOffset(2026, 8, 20, 10, 0, 1, TimeSpan.Zero),
            AttemptCount: 3,
            SuccessfulAttemptCount: 2,
            PacketLossRatio: 1m / 3m,
            MinRttMilliseconds: 1.5m,
            AverageRttMilliseconds: 2.5m,
            MaxRttMilliseconds: 3.5m,
            ErrorCategory: ProbeErrorCategory.Timeout));

    private sealed class FixedDiskCapacityProvider(OutboxDiskCapacity capacity) : IOutboxDiskCapacityProvider
    {
        public ValueTask<OutboxDiskCapacity> GetAsync(string databasePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(capacity);
    }

    private sealed class FailingMarkerStore : IOutboxRecoveryMarkerStore
    {
        public ValueTask<OutboxRecoveryState?> ReadAsync(string databasePath, CancellationToken cancellationToken) =>
            ValueTask.FromResult<OutboxRecoveryState?>(null);

        public ValueTask WriteAsync(string databasePath, OutboxRecoveryState state, CancellationToken cancellationToken) =>
            new(Task.FromException(new IOException("Synthetic marker write failure.")));
    }

    private sealed class BlockingDiskCapacityProvider(OutboxDiskCapacity capacity) : IOutboxDiskCapacityProvider
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<OutboxDiskCapacity> GetAsync(string databasePath, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return capacity;
        }

        public Task WaitUntilEnteredAsync(CancellationToken cancellationToken) => entered.Task.WaitAsync(cancellationToken);

        public void Release() => release.TrySetResult();
    }

    private sealed class FailingCompletionClaimStore : IOutboxSnapshotAdmissionClaimStore
    {
        public IOutboxSnapshotAdmissionClaim Acquire(string databasePath)
        {
            File.WriteAllText(databasePath + ".snapshot-admission", "ee-pulse-outbox-snapshot-admission-v1\n");
            return new FailingCompletionClaim();
        }
    }

    private sealed class FailingCompletionClaim : IOutboxSnapshotAdmissionClaim
    {
        public void Complete() => throw new IOException("Synthetic snapshot-admission claim cleanup failure.");

        public void Dispose()
        {
        }
    }
}
