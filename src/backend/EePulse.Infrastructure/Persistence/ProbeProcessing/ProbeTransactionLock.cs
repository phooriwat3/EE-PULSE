using Microsoft.EntityFrameworkCore;

namespace EePulse.Infrastructure.Persistence.ProbeProcessing;

public static class ProbeTransactionLock
{
    public static async Task AcquireAllAsync(
        EePulseDbContext db,
        IEnumerable<Guid> probeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(probeIds);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active EePulseDbContext database transaction is required to acquire a Probe transaction lock.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var orderedProbeIds = probeIds
            .Distinct()
            .OrderBy(probeId => probeId.ToString("D"), StringComparer.Ordinal)
            .ToArray();

        foreach (var probeId in orderedProbeIds)
        {
            await AcquireAsync(db, probeId, cancellationToken);
        }
    }

    public static async Task AcquireAsync(
        EePulseDbContext db,
        Guid probeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An active EePulseDbContext database transaction is required to acquire a Probe transaction lock.");
        }

        var canonicalProbeId = probeId.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({canonicalProbeId}, 0))",
            cancellationToken);
    }
}
