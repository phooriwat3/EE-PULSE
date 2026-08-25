using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EePulse.IntegrationTests;

public sealed class ProbeTransactionLockTests
{
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = CreateOptions(postgres.ConnectionString);
        var firstProbeId = Guid.NewGuid();
        var secondProbeId = Guid.NewGuid();

        await using var first = new EePulseDbContext(options);
        await using var firstTransaction = await first.Database.BeginTransactionAsync(ct);
        await ProbeTransactionLock.AcquireAsync(first, firstProbeId, ct);

        await using var second = new EePulseDbContext(options);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(ct);
        await ProbeTransactionLock.AcquireAsync(second, secondProbeId, ct);
        await secondTransaction.CommitAsync(ct);
        await firstTransaction.RollbackAsync(ct);
    }

    [Fact]
    public async Task MultipleProbeIdsAreAcquiredOnceInCanonicalOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = CreateOptions(postgres.ConnectionString);
        var probeIds = new[] { Guid.NewGuid(), Guid.NewGuid() }
            .OrderBy(probeId => probeId.ToString("D"), StringComparer.Ordinal)
            .ToArray();

        await using var first = new EePulseDbContext(options);
        await using var firstTransaction = await first.Database.BeginTransactionAsync(ct);
        await ProbeTransactionLock.AcquireAsync(first, probeIds[0], ct);

        await using var second = new EePulseDbContext(options);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(ct);
        var secondBackendProcessId = await second.Database
            .SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(ct);
        var secondAcquire = ProbeTransactionLock.AcquireAllAsync(second, [probeIds[1], probeIds[0], probeIds[1]], ct);
        var firstTransactionReleased = false;
        try
        {
            await using var observer = new NpgsqlConnection(postgres.ConnectionString);
            await observer.OpenAsync(ct);
            await WaitForUngrantedAdvisoryLockAsync(observer, secondBackendProcessId, probeIds[0], secondAcquire, ct);

            await using (var command = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = @backendProcessId AND granted", observer))
            {
                command.Parameters.AddWithValue("backendProcessId", secondBackendProcessId);
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(ct))!);
            }

            await firstTransaction.CommitAsync(ct);
            firstTransactionReleased = true;
            await secondAcquire;

            await using var finalCount = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND pid = @backendProcessId AND granted", observer);
            finalCount.Parameters.AddWithValue("backendProcessId", secondBackendProcessId);
            Assert.Equal(2L, (long)(await finalCount.ExecuteScalarAsync(ct))!);
            await secondTransaction.CommitAsync(ct);
        }
        finally
        {
            if (!firstTransactionReleased)
            {
                await RollbackIfActiveAsync(first, firstTransaction);
                await secondAcquire;
            }
        }
    }

    [Fact]
    public async Task AcquisitionWithoutAnActiveTransactionFailsFast()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var postgres = await PostgresTestDatabase.StartAsync(timeout.Token);
        await using var db = new EePulseDbContext(CreateOptions(postgres.ConnectionString));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProbeTransactionLock.AcquireAsync(db, Guid.NewGuid(), timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProbeTransactionLock.AcquireAllAsync(db, [Guid.NewGuid()], timeout.Token));
    }

    private static async Task AssertSameProbeBlocksUntilFirstTransactionReleasesAsync(
        Func<IDbContextTransaction, CancellationToken, Task> releaseFirstTransaction)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = CreateOptions(postgres.ConnectionString);
        var probeId = Guid.NewGuid();

        await using var first = new EePulseDbContext(options);
        await using var firstTransaction = await first.Database.BeginTransactionAsync(ct);
        await ProbeTransactionLock.AcquireAsync(first, probeId, ct);

        await using var second = new EePulseDbContext(options);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(ct);
        var secondBackendProcessId = await second.Database
            .SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(ct);
        var secondAcquire = ProbeTransactionLock.AcquireAsync(second, probeId, ct);

        var firstTransactionReleased = false;
        try
        {
            await using var observer = new NpgsqlConnection(postgres.ConnectionString);
            await observer.OpenAsync(ct);
            await WaitForUngrantedAdvisoryLockAsync(observer, secondBackendProcessId, probeId, secondAcquire, ct);

            await releaseFirstTransaction(firstTransaction, ct);
            firstTransactionReleased = true;
            await secondAcquire;
            await secondTransaction.CommitAsync(ct);
        }
        finally
        {
            if (!firstTransactionReleased)
            {
                await RollbackIfActiveAsync(first, firstTransaction);
            }

            if (!secondAcquire.IsCompleted)
            {
                try
                {
                    await secondAcquire.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    // The pending command honored this test's bounded cancellation.
                }
            }
        }
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

    private static Task RollbackIfActiveAsync(EePulseDbContext db, IDbContextTransaction transaction) =>
        db.Database.CurrentTransaction is not null && db.Database.GetDbConnection().State == System.Data.ConnectionState.Open
            ? transaction.RollbackAsync(CancellationToken.None)
            : Task.CompletedTask;

    private static DbContextOptions<EePulseDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
}
