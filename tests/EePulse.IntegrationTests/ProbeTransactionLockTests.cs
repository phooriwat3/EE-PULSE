using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EePulse.IntegrationTests;

public sealed class ProbeTransactionLockTests
{
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BehaviorTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SameProbeBlocksUntilFirstTransactionCommits()
    {
        await AssertSameProbeBlocksUntilFirstTransactionReleasesAsync(
            static (transaction, cancellationToken) => transaction.CommitAsync(cancellationToken));
    }

    [Fact]
    public async Task SameProbeBlocksUntilFirstTransactionRollsBack()
    {
        await AssertSameProbeBlocksUntilFirstTransactionReleasesAsync(
            static (transaction, cancellationToken) => transaction.RollbackAsync(cancellationToken));
    }

    [Fact]
    public async Task DifferentProbeIdsDoNotBlockEachOther()
    {
        using var preparationTimeout = new CancellationTokenSource(PreparationTimeout);
        var preparationToken = preparationTimeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(preparationToken);
        var options = CreateOptions(postgres.ConnectionString);
        var firstProbeId = Guid.NewGuid();
        var secondProbeId = Guid.NewGuid();

        await using var first = new EePulseDbContext(options);
        await using var firstTransaction = await first.Database.BeginTransactionAsync(preparationToken);
        await ProbeTransactionLock.AcquireAsync(first, firstProbeId, preparationToken);

        await using var second = new EePulseDbContext(options);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(preparationToken);
        using var behaviorTimeout = new CancellationTokenSource(BehaviorTimeout);
        var behaviorToken = behaviorTimeout.Token;
        var secondTransactionCommitted = false;
        Exception? testFailure = null;
        try
        {
            await ProbeTransactionLock.AcquireAsync(second, secondProbeId, behaviorToken);
            await secondTransaction.CommitAsync(behaviorToken);
            secondTransactionCommitted = true;
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(CleanupTimeout);
            var cleanupToken = cleanupTimeout.Token;
            var cleanupFailures = new List<Exception>();

            if (!secondTransactionCommitted)
            {
                try
                {
                    await RollbackIfActiveAsync(second, secondTransaction, cleanupToken);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            try
            {
                await RollbackIfActiveAsync(first, firstTransaction, cleanupToken);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            SurfaceCleanupFailures(cleanupFailures, testFailure);
        }
    }

    [Fact]
    public async Task MultipleProbeIdsAreAcquiredOnceInCanonicalOrder()
    {
        using var preparationTimeout = new CancellationTokenSource(PreparationTimeout);
        var preparationToken = preparationTimeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(preparationToken);
        var options = CreateOptions(postgres.ConnectionString);
        var probeIds = new[] { Guid.NewGuid(), Guid.NewGuid() }
            .OrderBy(probeId => probeId.ToString("D"), StringComparer.Ordinal)
            .ToArray();

        await using var first = new EePulseDbContext(options);
        await using var firstTransaction = await first.Database.BeginTransactionAsync(preparationToken);
        await ProbeTransactionLock.AcquireAsync(first, probeIds[0], preparationToken);

        await using var second = new EePulseDbContext(options);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(preparationToken);
        var secondBackendProcessId = await second.Database
            .SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(preparationToken);

        await using var observer = new NpgsqlConnection(postgres.ConnectionString);
        await observer.OpenAsync(preparationToken);
        using var behaviorTimeout = new CancellationTokenSource(BehaviorTimeout);
        var behaviorToken = behaviorTimeout.Token;
        var secondAcquire = ProbeTransactionLock.AcquireAllAsync(second, [probeIds[1], probeIds[0], probeIds[1]], behaviorToken);
        var firstTransactionReleased = false;
        Exception? testFailure = null;
        try
        {
            await WaitForUngrantedAdvisoryLockAsync(observer, secondBackendProcessId, probeIds[0], secondAcquire, behaviorToken);

            await using (var command = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = @backendProcessId AND granted", observer))
            {
                command.Parameters.AddWithValue("backendProcessId", secondBackendProcessId);
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(behaviorToken))!);
            }

            await firstTransaction.CommitAsync(behaviorToken);
            firstTransactionReleased = true;
            await secondAcquire;

            await using var finalCount = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = @backendProcessId AND granted", observer);
            finalCount.Parameters.AddWithValue("backendProcessId", secondBackendProcessId);
            Assert.Equal(2L, (long)(await finalCount.ExecuteScalarAsync(behaviorToken))!);
            await secondTransaction.CommitAsync(behaviorToken);
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(CleanupTimeout);
            await CleanupTransactionsAndAcquisitionAsync(
                first,
                firstTransaction,
                firstTransactionReleased,
                second,
                secondTransaction,
                secondAcquire,
                behaviorTimeout,
                testFailure,
                cleanupTimeout.Token);
        }
    }

    [Fact]
    public async Task AcquisitionWithoutAnActiveTransactionFailsFast()
    {
        using var preparationTimeout = new CancellationTokenSource(PreparationTimeout);
        await using var postgres = await PostgresTestDatabase.StartAsync(preparationTimeout.Token);
        await using var db = new EePulseDbContext(CreateOptions(postgres.ConnectionString));
        using var behaviorTimeout = new CancellationTokenSource(BehaviorTimeout);
        var behaviorToken = behaviorTimeout.Token;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProbeTransactionLock.AcquireAsync(db, Guid.NewGuid(), behaviorToken));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProbeTransactionLock.AcquireAllAsync(db, [Guid.NewGuid()], behaviorToken));
    }

    private static async Task AssertSameProbeBlocksUntilFirstTransactionReleasesAsync(
        Func<IDbContextTransaction, CancellationToken, Task> releaseFirstTransaction)
    {
        using var preparationTimeout = new CancellationTokenSource(PreparationTimeout);
        var preparationToken = preparationTimeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(preparationToken);
        var options = CreateOptions(postgres.ConnectionString);
        var probeId = Guid.NewGuid();

        await using var first = new EePulseDbContext(options);
        await using var firstTransaction = await first.Database.BeginTransactionAsync(preparationToken);
        await ProbeTransactionLock.AcquireAsync(first, probeId, preparationToken);

        await using var second = new EePulseDbContext(options);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(preparationToken);
        var secondBackendProcessId = await second.Database
            .SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(preparationToken);

        await using var observer = new NpgsqlConnection(postgres.ConnectionString);
        await observer.OpenAsync(preparationToken);
        using var behaviorTimeout = new CancellationTokenSource(BehaviorTimeout);
        var behaviorToken = behaviorTimeout.Token;
        var secondAcquire = ProbeTransactionLock.AcquireAsync(second, probeId, behaviorToken);

        var firstTransactionReleased = false;
        Exception? testFailure = null;
        try
        {
            await WaitForUngrantedAdvisoryLockAsync(observer, secondBackendProcessId, probeId, secondAcquire, behaviorToken);

            await releaseFirstTransaction(firstTransaction, behaviorToken);
            firstTransactionReleased = true;
            await secondAcquire;
            await secondTransaction.CommitAsync(behaviorToken);
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(CleanupTimeout);
            await CleanupTransactionsAndAcquisitionAsync(
                first,
                firstTransaction,
                firstTransactionReleased,
                second,
                secondTransaction,
                secondAcquire,
                behaviorTimeout,
                testFailure,
                cleanupTimeout.Token);
        }
    }

    private static async Task CleanupTransactionsAndAcquisitionAsync(
        EePulseDbContext first,
        IDbContextTransaction firstTransaction,
        bool firstTransactionReleased,
        EePulseDbContext second,
        IDbContextTransaction secondTransaction,
        Task secondAcquire,
        CancellationTokenSource behaviorTimeout,
        Exception? testFailure,
        CancellationToken cleanupToken)
    {
        var cleanupFailures = new List<Exception>();

        if (!firstTransactionReleased)
        {
            try
            {
                await RollbackIfActiveAsync(first, firstTransaction, cleanupToken);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        try
        {
            if (!secondAcquire.IsCompleted && !behaviorTimeout.IsCancellationRequested)
            {
                try
                {
                    behaviorTimeout.Cancel();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            await secondAcquire.WaitAsync(cleanupToken);
        }
        catch (OperationCanceledException) when (behaviorTimeout.IsCancellationRequested || cleanupToken.IsCancellationRequested)
        {
            if (!secondAcquire.IsCompleted && cleanupToken.IsCancellationRequested)
            {
                cleanupFailures.Add(new TimeoutException("The pending lock acquisition did not settle within the bounded cleanup deadline."));
            }
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }
        finally
        {
            // Never roll back on the second connection while its acquisition is still using that DbContext.
            if (secondAcquire.IsCompleted)
            {
                try
                {
                    await RollbackIfActiveAsync(second, secondTransaction, cleanupToken);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
        }

        SurfaceCleanupFailures(cleanupFailures, testFailure);
    }

    private static void SurfaceCleanupFailures(List<Exception> cleanupFailures, Exception? testFailure)
    {
        if (cleanupFailures.Count == 0)
        {
            return;
        }

        var cleanupFailure = cleanupFailures.Count == 1
            ? cleanupFailures[0]
            : new AggregateException("Multiple transaction-lock cleanup operations failed.", cleanupFailures);

        if (testFailure is not null)
        {
            testFailure.Data["CleanupFailure"] = cleanupFailure;
            return;
        }

        throw cleanupFailure;
    }

    private static async Task WaitForUngrantedAdvisoryLockAsync(
        NpgsqlConnection observer,
        int backendProcessId,
        Guid probeId,
        Task acquiring,
        CancellationToken cancellationToken)
    {
        var canonicalProbeId = probeId.ToString("D");
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND pid = @backendProcessId
                      AND NOT granted
                      AND (lpad(to_hex(classid::bigint), 8, '0') || lpad(to_hex(objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0'))
                """, observer);
            command.Parameters.AddWithValue("backendProcessId", backendProcessId);
            command.Parameters.AddWithValue("probeId", canonicalProbeId);

            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                return;
            }

            if (acquiring.IsCompleted)
            {
                await acquiring;
                throw new Xunit.Sdk.XunitException("The second transaction acquired the same-Probe advisory lock before the first transaction released it.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static Task RollbackIfActiveAsync(EePulseDbContext db, IDbContextTransaction transaction, CancellationToken cancellationToken) =>
        db.Database.CurrentTransaction is not null && db.Database.GetDbConnection().State == System.Data.ConnectionState.Open
            ? transaction.RollbackAsync(cancellationToken)
            : Task.CompletedTask;

    private static DbContextOptions<EePulseDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
}
