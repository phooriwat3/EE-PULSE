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
    public async Task AcquisitionWithoutAnActiveTransactionFailsFast()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var postgres = await PostgresTestDatabase.StartAsync(timeout.Token);
        await using var db = new EePulseDbContext(CreateOptions(postgres.ConnectionString));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProbeTransactionLock.AcquireAsync(db, Guid.NewGuid(), timeout.Token));
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
            await WaitForUngrantedAdvisoryLockAsync(observer, secondBackendProcessId, secondAcquire, ct);

            await releaseFirstTransaction(firstTransaction, ct);
            firstTransactionReleased = true;
            await secondAcquire;
            await secondTransaction.CommitAsync(ct);
        }
        finally
        {
            if (!firstTransactionReleased)
            {
                try
                {
                    await firstTransaction.RollbackAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // The release operation may have completed before throwing.
                }
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
        Task acquiring,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND pid = @backendProcessId
                      AND NOT granted)
                """, observer);
            command.Parameters.AddWithValue("backendProcessId", backendProcessId);

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

    private static DbContextOptions<EePulseDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;
}
