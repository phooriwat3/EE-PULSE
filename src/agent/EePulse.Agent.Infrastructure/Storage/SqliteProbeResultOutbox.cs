using System.Text.Json;
using EePulse.Agent.Core.Outbox;
using EePulse.Agent.Core.Probing;
using Microsoft.Data.Sqlite;

namespace EePulse.Agent.Infrastructure.Storage;

public enum OutboxRecoveryStatus
{
    CorruptQuarantined,
    CorruptQuarantineFailed,
    UnsupportedSchema,
    SnapshotNotAdmitted,
    SnapshotCleanupFailed,
    SnapshotAdmissionClaimed,
    RecoveryMarkerFailed,
}

public sealed record OutboxRecoveryState(OutboxRecoveryStatus Status, string? EvidencePath);

public sealed record OutboxDiskCapacity(long TotalBytes, long AvailableBytes);

public interface IOutboxDiskCapacityProvider
{
    ValueTask<OutboxDiskCapacity> GetAsync(string databasePath, CancellationToken cancellationToken);
}

public interface IOutboxRecoveryMarkerStore
{
    ValueTask<OutboxRecoveryState?> ReadAsync(string databasePath, CancellationToken cancellationToken);

    ValueTask WriteAsync(string databasePath, OutboxRecoveryState state, CancellationToken cancellationToken);
}

public interface IOutboxSnapshotAdmissionClaim : IDisposable
{
    void Complete();
}

public interface IOutboxSnapshotAdmissionClaimStore
{
    IOutboxSnapshotAdmissionClaim Acquire(string databasePath);
}

public static class OutboxEvidenceSnapshotAdmission
{
    public static long CalculateEvidenceBytes(IEnumerable<long> fileLengths)
    {
        ArgumentNullException.ThrowIfNull(fileLengths);
        long total = 0;
        foreach (var length in fileLengths)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(length, 0);
            total = checked(total + length);
        }

        return total;
    }

    public static bool CanAdmit(OutboxDiskCapacity capacity, long evidenceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity.TotalBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity.AvailableBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(evidenceBytes, 0);
        var reserve = Math.Max(OutboxStoragePressure.MinimumReserveBytes, capacity.TotalBytes / 10);
        return capacity.AvailableBytes >= reserve && capacity.AvailableBytes - reserve >= evidenceBytes;
    }
}

public sealed class OutboxCorruptionException(OutboxRecoveryState recovery) : Exception(
    "The Agent result outbox is unavailable and requires operator recovery.")
{
    public OutboxRecoveryState Recovery { get; } = recovery;
}

public sealed class OutboxUnsupportedSchemaException(int resultSchemaVersion) : Exception(
    "The Agent result outbox contains an unsupported result schema and requires operator recovery.")
{
    public int ResultSchemaVersion { get; } = resultSchemaVersion;
}

/// <summary>SQLite WAL-backed durable storage for immutable local probe results.</summary>
public sealed class SqliteProbeResultOutbox : IProbeResultOutbox
{
    private const int CleanupWindowHours = 24;
    private readonly string databasePath;
    private readonly IOutboxDiskCapacityProvider diskCapacityProvider;
    private readonly IOutboxRecoveryMarkerStore recoveryMarkerStore;
    private readonly IOutboxSnapshotAdmissionClaimStore snapshotAdmissionClaimStore;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private readonly SemaphoreSlim writes = new(1, 1);
    private Exception? unavailable;
    private bool initialized;

    public SqliteProbeResultOutbox(
        string databasePath,
        IOutboxDiskCapacityProvider? diskCapacityProvider = null,
        IOutboxRecoveryMarkerStore? recoveryMarkerStore = null,
        IOutboxSnapshotAdmissionClaimStore? snapshotAdmissionClaimStore = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("An absolute outbox database path is required.", nameof(databasePath));
        }

        this.databasePath = databasePath;
        this.diskCapacityProvider = diskCapacityProvider ?? new SystemOutboxDiskCapacityProvider();
        this.recoveryMarkerStore = recoveryMarkerStore ?? new FileOutboxRecoveryMarkerStore();
        this.snapshotAdmissionClaimStore = snapshotAdmissionClaimStore ?? new FileOutboxSnapshotAdmissionClaimStore();
    }

    public async ValueTask<ProbeResultOutboxRecord> EnqueueAsync(ProbeResultEnvelope envelope, CancellationToken cancellationToken)
    {
        ValidateEnvelope(envelope);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureStoredSchemasAreSupportedAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var enqueuedAt = DateTimeOffset.UtcNow;
            var payload = JsonSerializer.Serialize(envelope);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO probe_result_outbox (
                    result_id, result_schema_version, agent_id, probe_id, configuration_version,
                    started_at_ticks, ended_at_ticks, attempt_count, successful_attempt_count, packet_loss_ratio,
                    min_rtt_milliseconds, average_rtt_milliseconds, max_rtt_milliseconds, error_category,
                    payload, serialized_byte_count, state, enqueued_at_ticks)
                VALUES (
                    $resultId, $schemaVersion, $agentId, $probeId, $configurationVersion,
                    $startedAt, $endedAt, $attemptCount, $successfulAttemptCount, $packetLossRatio,
                    $minRtt, $averageRtt, $maxRtt, $errorCategory, $payload, $serializedByteCount, 0, $enqueuedAt);
                SELECT last_insert_rowid();
                """;
            BindEnvelope(command, envelope, payload, bytes);
            command.Parameters.AddWithValue("$enqueuedAt", enqueuedAt.UtcTicks);
            var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(sequence, envelope, ProbeResultOutboxState.Pending, enqueuedAt, null, null, null, bytes);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("A result with this immutable result ID already exists.", exception);
        }
        finally
        {
            writes.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ProbeResultOutboxRecord>> ReadPendingAsync(
        ProbeResultOutboxReadLimit limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limit);
        limit.Validate();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, result_schema_version, result_id, agent_id, probe_id, configuration_version,
                   started_at_ticks, ended_at_ticks, attempt_count, successful_attempt_count, packet_loss_ratio,
                   min_rtt_milliseconds, average_rtt_milliseconds, max_rtt_milliseconds, error_category,
                   enqueued_at_ticks, serialized_byte_count
            FROM probe_result_outbox
            WHERE state = 0
            ORDER BY sequence ASC
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", limit.MaximumCount);
        var records = new List<ProbeResultOutboxRecord>(limit.MaximumCount);
        var totalBytes = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = ReadRecord(reader);
            if (record.SerializedByteCount > limit.MaximumSerializedBytes - totalBytes)
            {
                break;
            }

            records.Add(record);
            totalBytes += record.SerializedByteCount;
        }

        return records;
    }

    public async ValueTask AcknowledgeAsync(
        IReadOnlyCollection<Guid> resultIds,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resultIds);
        if (resultIds.Count == 0) return;
        if (resultIds.Any(id => id == Guid.Empty) || resultIds.Distinct().Count() != resultIds.Count)
        {
            throw new ArgumentException("Acknowledgements must name distinct non-empty result IDs.", nameof(resultIds));
        }

        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureStoredSchemasAreSupportedAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var resultId in resultIds)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE probe_result_outbox
                    SET state = 1,
                        acknowledged_at_ticks = CASE WHEN state = 0 THEN $acknowledgedAt ELSE acknowledged_at_ticks END,
                        cleanup_eligible_at_ticks = CASE WHEN state = 0 THEN $cleanupEligibleAt ELSE cleanup_eligible_at_ticks END,
                        cleanup_deadline_at_ticks = CASE WHEN state = 0 THEN $cleanupDeadlineAt ELSE cleanup_deadline_at_ticks END
                    WHERE result_id = $resultId AND state IN (0, 1);
                    """;
                command.Parameters.AddWithValue("$resultId", resultId.ToString("D"));
                command.Parameters.AddWithValue("$acknowledgedAt", acknowledgedAt.UtcTicks);
                command.Parameters.AddWithValue("$cleanupEligibleAt", acknowledgedAt.UtcTicks);
                command.Parameters.AddWithValue("$cleanupDeadlineAt", acknowledgedAt.AddHours(CleanupWindowHours).UtcTicks);
                var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new InvalidOperationException("An acknowledgement named an unknown result.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writes.Release();
        }
    }

    public async ValueTask ApplyDeliveryOutcomeAsync(
        IReadOnlyCollection<Guid> acceptedResultIds,
        IReadOnlyCollection<ProbeResultPermanentRejection> permanentRejections,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptedResultIds);
        ArgumentNullException.ThrowIfNull(permanentRejections);
        var accepted = acceptedResultIds.ToHashSet();
        var rejected = permanentRejections.ToDictionary(rejection => rejection.ResultId);
        if (accepted.Any(id => id == Guid.Empty) || rejected.Keys.Any(id => id == Guid.Empty) || accepted.Overlaps(rejected.Keys) ||
            permanentRejections.Count != rejected.Count || permanentRejections.Any(rejection => string.IsNullOrWhiteSpace(rejection.ReasonCode)))
        {
            throw new ArgumentException("Delivery outcomes must name distinct non-empty result IDs with fixed reason codes.");
        }

        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureStoredSchemasAreSupportedAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var resultId in accepted)
            {
                await MarkAcknowledgedAsync(connection, transaction, resultId, processedAt, cancellationToken).ConfigureAwait(false);
            }

            foreach (var rejection in rejected.Values)
            {
                await using var quarantine = connection.CreateCommand();
                quarantine.Transaction = transaction;
                quarantine.CommandText = """
                    INSERT INTO probe_result_outbox_quarantine (result_id, reason_code, payload, quarantined_at_ticks)
                    SELECT result_id, $reasonCode, payload, $processedAt
                    FROM probe_result_outbox WHERE result_id = $resultId AND state = 0;
                    """;
                quarantine.Parameters.AddWithValue("$reasonCode", rejection.ReasonCode);
                quarantine.Parameters.AddWithValue("$processedAt", processedAt.UtcTicks);
                quarantine.Parameters.AddWithValue("$resultId", rejection.ResultId.ToString("D"));
                if (await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("A permanent rejection named an unknown or non-pending result.");
                }

                await using var remove = connection.CreateCommand();
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM probe_result_outbox WHERE result_id = $resultId AND state = 0;";
                remove.Parameters.AddWithValue("$resultId", rejection.ResultId.ToString("D"));
                if (await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("A permanent rejection could not be quarantined safely.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writes.Release();
        }
    }

    public async ValueTask<int> CleanupAcknowledgedAsync(DateTimeOffset cleanupThrough, int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureStoredSchemasAreSupportedAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM probe_result_outbox
                WHERE sequence IN (
                    SELECT sequence FROM probe_result_outbox
                    WHERE state = 1 AND cleanup_eligible_at_ticks <= $cleanupThrough
                    ORDER BY sequence ASC
                    LIMIT $maximumCount);
                """;
            command.Parameters.AddWithValue("$cleanupThrough", cleanupThrough.UtcTicks);
            command.Parameters.AddWithValue("$maximumCount", maximumCount);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writes.Release();
        }
    }

    private static async ValueTask MarkAcknowledgedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid resultId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE probe_result_outbox
            SET state = 1,
                acknowledged_at_ticks = CASE WHEN state = 0 THEN $acknowledgedAt ELSE acknowledged_at_ticks END,
                cleanup_eligible_at_ticks = CASE WHEN state = 0 THEN $cleanupEligibleAt ELSE cleanup_eligible_at_ticks END,
                cleanup_deadline_at_ticks = CASE WHEN state = 0 THEN $cleanupDeadlineAt ELSE cleanup_deadline_at_ticks END
            WHERE result_id = $resultId AND state IN (0, 1);
            """;
        command.Parameters.AddWithValue("$resultId", resultId.ToString("D"));
        command.Parameters.AddWithValue("$acknowledgedAt", acknowledgedAt.UtcTicks);
        command.Parameters.AddWithValue("$cleanupEligibleAt", acknowledgedAt.UtcTicks);
        command.Parameters.AddWithValue("$cleanupDeadlineAt", acknowledgedAt.AddHours(CleanupWindowHours).UtcTicks);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("An acknowledgement named an unknown result.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await initialization.WaitAsync().ConfigureAwait(false);
        initialized = false;
        initialization.Release();
        initialization.Dispose();
        writes.Dispose();
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref unavailable) is { } failure) throw failure;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref initialized)) return;
        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            var directory = Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("The Agent result outbox path is invalid.");
            Directory.CreateDirectory(directory);
            IOutboxSnapshotAdmissionClaim claim;
            try
            {
                claim = snapshotAdmissionClaimStore.Acquire(databasePath);
            }
            catch (Exception)
            {
                try
                {
                    var marker = await recoveryMarkerStore.ReadAsync(databasePath, CancellationToken.None).ConfigureAwait(false);
                    if (marker is not null)
                    {
                        throw FailClosed(new OutboxCorruptionException(marker));
                    }

                    if (FindExistingSnapshot() is { } residualEvidencePath)
                    {
                        throw FailClosed(new OutboxCorruptionException(
                            new(OutboxRecoveryStatus.SnapshotNotAdmitted, residualEvidencePath)));
                    }
                }
                catch (OutboxCorruptionException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw FailClosed(new OutboxCorruptionException(new(OutboxRecoveryStatus.RecoveryMarkerFailed, null)));
                }

                throw FailClosed(new OutboxCorruptionException(
                    new(OutboxRecoveryStatus.SnapshotAdmissionClaimed, databasePath + ".snapshot-admission")));
            }

            using (claim)
            {
                // From this point the durable claim, rather than caller cancellation, governs recovery safety.
                var transitionCancellationToken = CancellationToken.None;
                OutboxRecoveryState? marker;
                try
                {
                    marker = await recoveryMarkerStore.ReadAsync(databasePath, transitionCancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw FailClosed(new OutboxCorruptionException(new(OutboxRecoveryStatus.RecoveryMarkerFailed, null)));
                }

                if (marker is not null)
                {
                    throw FailClosed(new OutboxCorruptionException(marker));
                }

                if (FindExistingSnapshot() is { } residualEvidencePath)
                {
                    throw await FailWithRecoveryMarkerAsync(
                        new(OutboxRecoveryStatus.SnapshotNotAdmitted, residualEvidencePath), transitionCancellationToken).ConfigureAwait(false);
                }

                var existingDatabase = File.Exists(databasePath);
                if (existingDatabase)
                {
                    string? preflightEvidencePath = null;
                    if (new FileInfo(databasePath).Length == 0)
                    {
                        throw await QuarantineAndFailClosedAsync(null, transitionCancellationToken).ConfigureAwait(false);
                    }

                    try
                    {
                        preflightEvidencePath = await CreatePreflightEvidenceSnapshotAsync(transitionCancellationToken).ConfigureAwait(false);
                    }
                    catch (OutboxCorruptionException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        throw await FailWithRecoveryMarkerAsync(
                            new(OutboxRecoveryStatus.SnapshotNotAdmitted, preflightEvidencePath), transitionCancellationToken).ConfigureAwait(false);
                    }

                    if (preflightEvidencePath is null)
                    {
                        throw await FailWithRecoveryMarkerAsync(
                            new(OutboxRecoveryStatus.SnapshotNotAdmitted, null), transitionCancellationToken).ConfigureAwait(false);
                    }

                    try
                    {
                        await ValidateExistingDatabaseAsync(transitionCancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        throw await QuarantineAndFailClosedAsync(preflightEvidencePath, transitionCancellationToken).ConfigureAwait(false);
                    }

                    var unsupportedSchemaVersion = await FindUnsupportedSchemaVersionAsync(transitionCancellationToken).ConfigureAwait(false);
                    if (unsupportedSchemaVersion is { } version)
                    {
                        DeletePreflightEvidenceSnapshot(preflightEvidencePath);
                        throw FailClosed(new OutboxUnsupportedSchemaException(version));
                    }

                    try
                    {
                        DeletePreflightEvidenceSnapshot(preflightEvidencePath);
                    }
                    catch (Exception)
                    {
                        throw await FailWithRecoveryMarkerAsync(
                            new(OutboxRecoveryStatus.SnapshotCleanupFailed, preflightEvidencePath), transitionCancellationToken).ConfigureAwait(false);
                    }
                }

                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = existingDatabase ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared,
                    DefaultTimeout = 5,
                }.ToString();
                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync(transitionCancellationToken).ConfigureAwait(false);
                    await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", transitionCancellationToken).ConfigureAwait(false);
                    await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", transitionCancellationToken).ConfigureAwait(false);
                    await ExecuteAsync(connection, "PRAGMA synchronous = FULL;", transitionCancellationToken).ConfigureAwait(false);
                    await ExecuteAsync(connection, """
                    CREATE TABLE IF NOT EXISTS probe_result_outbox (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        result_id TEXT NOT NULL UNIQUE,
                        result_schema_version INTEGER NOT NULL,
                        agent_id TEXT NOT NULL,
                        probe_id TEXT NOT NULL,
                        configuration_version INTEGER NOT NULL,
                        started_at_ticks INTEGER NOT NULL,
                        ended_at_ticks INTEGER NOT NULL,
                        attempt_count INTEGER NOT NULL,
                        successful_attempt_count INTEGER NOT NULL,
                        packet_loss_ratio TEXT NOT NULL,
                        min_rtt_milliseconds TEXT NULL,
                        average_rtt_milliseconds TEXT NULL,
                        max_rtt_milliseconds TEXT NULL,
                        error_category INTEGER NULL,
                        payload TEXT NOT NULL,
                        serialized_byte_count INTEGER NOT NULL,
                        state INTEGER NOT NULL,
                        enqueued_at_ticks INTEGER NOT NULL,
                        acknowledged_at_ticks INTEGER NULL,
                        cleanup_eligible_at_ticks INTEGER NULL,
                        cleanup_deadline_at_ticks INTEGER NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_probe_result_outbox_pending
                        ON probe_result_outbox (state, sequence);
                    CREATE TABLE IF NOT EXISTS probe_result_outbox_quarantine (
                        result_id TEXT PRIMARY KEY,
                        reason_code TEXT NOT NULL,
                        payload TEXT NOT NULL,
                        quarantined_at_ticks INTEGER NOT NULL
                    );
                    """, transitionCancellationToken).ConfigureAwait(false);
                }

                try
                {
                    claim.Complete();
                }
                catch (Exception)
                {
                    throw await FailWithRecoveryMarkerAsync(
                        new(OutboxRecoveryStatus.SnapshotCleanupFailed, databasePath + ".snapshot-admission"), transitionCancellationToken).ConfigureAwait(false);
                }

                initialized = true;
            }
        }
        finally
        {
            initialization.Release();
        }
    }

    private async ValueTask ValidateExistingDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        var checkResult = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        await using var metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'probe_result_outbox';";
        if (await metadata.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) throw new InvalidDataException();
    }

    private async ValueTask<string?> CreatePreflightEvidenceSnapshotAsync(CancellationToken cancellationToken)
    {
        var lengths = new List<long>(3);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var source = databasePath + suffix;
            if (File.Exists(source)) lengths.Add(new FileInfo(source).Length);
        }

        var evidenceBytes = OutboxEvidenceSnapshotAdmission.CalculateEvidenceBytes(lengths);
        var capacity = await diskCapacityProvider.GetAsync(databasePath, cancellationToken).ConfigureAwait(false);
        if (!OutboxEvidenceSnapshotAdmission.CanAdmit(capacity, evidenceBytes)) return null;
        var evidencePath = Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            $"{Path.GetFileName(databasePath)}.quarantine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidencePath);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = databasePath + suffix;
            if (File.Exists(source)) File.Copy(source, Path.Combine(evidencePath, Path.GetFileName(source)));
        }

        return evidencePath;
    }

    private static void DeletePreflightEvidenceSnapshot(string? evidencePath)
    {
        if (evidencePath is not null && Directory.Exists(evidencePath)) Directory.Delete(evidencePath, recursive: true);
    }

    private async ValueTask<OutboxCorruptionException> QuarantineAndFailClosedAsync(
        string? preflightEvidencePath,
        CancellationToken cancellationToken)
    {
        var evidencePath = preflightEvidencePath ?? Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            $"{Path.GetFileName(databasePath)}.quarantine-{Guid.NewGuid():N}");
        try
        {
            var markerFailure = await TryWriteRecoveryMarkerAsync(
                new(OutboxRecoveryStatus.CorruptQuarantined, evidencePath), cancellationToken).ConfigureAwait(false);
            if (markerFailure is not null) return markerFailure;
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(evidencePath);
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var source = databasePath + suffix;
                if (!File.Exists(source)) continue;
                var destinationName = preflightEvidencePath is null
                    ? Path.GetFileName(source)
                    : $"post-validation-{Path.GetFileName(source)}";
                File.Move(source, Path.Combine(evidencePath, destinationName));
            }

            return (OutboxCorruptionException)FailClosed(new OutboxCorruptionException(
                new(OutboxRecoveryStatus.CorruptQuarantined, evidencePath)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (OutboxCorruptionException)FailClosed(new OutboxCorruptionException(
                new(OutboxRecoveryStatus.CorruptQuarantineFailed, evidencePath)));
        }
    }

    private Exception FailClosed(Exception failure) => Interlocked.CompareExchange(ref unavailable, failure, null) ?? failure;

    private async ValueTask<OutboxCorruptionException> FailWithRecoveryMarkerAsync(
        OutboxRecoveryState state,
        CancellationToken cancellationToken)
    {
        var markerFailure = await TryWriteRecoveryMarkerAsync(state, cancellationToken).ConfigureAwait(false);
        return markerFailure ?? (OutboxCorruptionException)FailClosed(new OutboxCorruptionException(state));
    }

    private async ValueTask<OutboxCorruptionException?> TryWriteRecoveryMarkerAsync(
        OutboxRecoveryState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await recoveryMarkerStore.WriteAsync(databasePath, state, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (OutboxCorruptionException)FailClosed(new OutboxCorruptionException(
                new(OutboxRecoveryStatus.RecoveryMarkerFailed, state.EvidencePath)));
        }
    }

    private string? FindExistingSnapshot()
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        var prefix = Path.GetFileName(databasePath) + ".quarantine-";
        return Directory.EnumerateDirectories(directory)
            .FirstOrDefault(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal));
    }

    private async ValueTask<int?> FindUnsupportedSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_schema_version FROM probe_result_outbox WHERE result_schema_version <> $supported LIMIT 1;";
        command.Parameters.AddWithValue("$supported", ProbeResultSchema.CurrentVersion);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? null : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async ValueTask EnsureStoredSchemasAreSupportedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_schema_version FROM probe_result_outbox WHERE result_schema_version <> $supported LIMIT 1;";
        command.Parameters.AddWithValue("$supported", ProbeResultSchema.CurrentVersion);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            throw FailClosed(new OutboxUnsupportedSchemaException(
                Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindEnvelope(SqliteCommand command, ProbeResultEnvelope envelope, string payload, int bytes)
    {
        command.Parameters.AddWithValue("$resultId", envelope.ResultId.ToString("D"));
        command.Parameters.AddWithValue("$schemaVersion", envelope.ResultSchemaVersion);
        command.Parameters.AddWithValue("$agentId", envelope.AgentId.ToString("D"));
        command.Parameters.AddWithValue("$probeId", envelope.ProbeId.ToString("D"));
        command.Parameters.AddWithValue("$configurationVersion", envelope.ConfigurationVersion);
        command.Parameters.AddWithValue("$startedAt", envelope.StartedAt.UtcTicks);
        command.Parameters.AddWithValue("$endedAt", envelope.EndedAt.UtcTicks);
        command.Parameters.AddWithValue("$attemptCount", envelope.AttemptCount);
        command.Parameters.AddWithValue("$successfulAttemptCount", envelope.SuccessfulAttemptCount);
        command.Parameters.AddWithValue("$packetLossRatio", envelope.PacketLossRatio.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$minRtt", (object?)envelope.MinRttMilliseconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$averageRtt", (object?)envelope.AverageRttMilliseconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$maxRtt", (object?)envelope.MaxRttMilliseconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCategory", (object?)envelope.ErrorCategory is { } error ? (int)error : DBNull.Value);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$serializedByteCount", bytes);
    }

    private ProbeResultOutboxRecord ReadRecord(SqliteDataReader reader)
    {
        var resultSchemaVersion = reader.GetInt32(1);
        if (resultSchemaVersion != ProbeResultSchema.CurrentVersion)
        {
            throw FailClosed(new OutboxUnsupportedSchemaException(resultSchemaVersion));
        }

        var envelope = new ProbeResultEnvelope(
            resultSchemaVersion, Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)),
            reader.GetInt64(5), FromTicks(reader.GetInt64(6)), FromTicks(reader.GetInt64(7)), reader.GetInt32(8), reader.GetInt32(9),
            decimal.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture),
            ReadDecimal(reader, 11), ReadDecimal(reader, 12), ReadDecimal(reader, 13),
            reader.IsDBNull(14) ? null : (ProbeErrorCategory)reader.GetInt32(14));
        return new(reader.GetInt64(0), envelope, ProbeResultOutboxState.Pending, FromTicks(reader.GetInt64(15)), null, null, null, reader.GetInt32(16));
    }

    private static decimal? ReadDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : decimal.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    private static void ValidateEnvelope(ProbeResultEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.ResultSchemaVersion != ProbeResultSchema.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope));
        }
        ArgumentOutOfRangeException.ThrowIfEqual(envelope.ResultId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(envelope.AgentId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(envelope.ProbeId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(envelope.ConfigurationVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(envelope.AttemptCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(envelope.SuccessfulAttemptCount, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(envelope.SuccessfulAttemptCount, envelope.AttemptCount);
        if (envelope.PacketLossRatio is < 0 or > 1 || envelope.EndedAt < envelope.StartedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope));
        }
    }
}

internal sealed class SystemOutboxDiskCapacityProvider : IOutboxDiskCapacityProvider
{
    public ValueTask<OutboxDiskCapacity> GetAsync(string databasePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetPathRoot(Path.GetFullPath(databasePath)) ?? throw new InvalidOperationException("The Agent result outbox path is invalid.");
        var drive = new DriveInfo(root);
        return ValueTask.FromResult(new OutboxDiskCapacity(drive.TotalSize, drive.AvailableFreeSpace));
    }
}

internal sealed class FileOutboxRecoveryMarkerStore : IOutboxRecoveryMarkerStore
{
    public async ValueTask<OutboxRecoveryState?> ReadAsync(string databasePath, CancellationToken cancellationToken)
    {
        var markerPath = MarkerPath(databasePath);
        if (!File.Exists(markerPath)) return null;
        var bytes = await File.ReadAllBytesAsync(markerPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<OutboxRecoveryState>(bytes) ?? throw new InvalidDataException();
    }

    public async ValueTask WriteAsync(string databasePath, OutboxRecoveryState state, CancellationToken cancellationToken)
    {
        var markerPath = MarkerPath(databasePath);
        var directory = Path.GetDirectoryName(markerPath) ?? throw new InvalidOperationException("The Agent result outbox path is invalid.");
        Directory.CreateDirectory(directory);
        var temporaryPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string MarkerPath(string databasePath) => databasePath + ".recovery-required";
}

internal sealed class FileOutboxSnapshotAdmissionClaimStore : IOutboxSnapshotAdmissionClaimStore
{
    public IOutboxSnapshotAdmissionClaim Acquire(string databasePath)
    {
        var claimPath = databasePath + ".snapshot-admission";
        var stream = new FileStream(
            claimPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        try
        {
            var content = System.Text.Encoding.UTF8.GetBytes("ee-pulse-outbox-snapshot-admission-v1\n");
            stream.Write(content);
            stream.Flush(flushToDisk: true);
            return new FileOutboxSnapshotAdmissionClaim(claimPath, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}

internal sealed class FileOutboxSnapshotAdmissionClaim(string claimPath, FileStream claimStream) : IOutboxSnapshotAdmissionClaim
{
    private FileStream? stream = claimStream;
    private bool completed;

    public void Complete()
    {
        if (completed) return;
        stream?.Dispose();
        stream = null;
        File.Delete(claimPath);
        completed = true;
    }

    public void Dispose() => stream?.Dispose();
}
