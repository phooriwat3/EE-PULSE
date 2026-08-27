using EePulse.Domain.Status;
using Microsoft.EntityFrameworkCore;

namespace EePulse.Infrastructure.Persistence.ProbeProcessing;

public enum ProbeFreshnessExpiryProcessorOutcomeKind
{
    NoDueCause,
    Applied,
    NoOp,
}

public sealed record ProbeFreshnessExpiryProcessorOutcome(
    ProbeFreshnessExpiryProcessorOutcomeKind Kind,
    Guid? CauseId = null,
    ProbeFreshnessExpiryCauseDispositionOutcome? DispositionOutcome = null,
    string? ReasonCode = null);

public sealed class ProbeFreshnessExpiryCauseProcessor(EePulseDbContext db)
{
    public async Task<ProbeFreshnessExpiryProcessorOutcome> ProcessNextDueAsync(
        Guid probeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(probeId, Guid.Empty);

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await ProbeTransactionLock.AcquireAsync(db, probeId, cancellationToken);

        var expiryCutoffReceivedAt = await db.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT date_trunc('microseconds', clock_timestamp()) AS \"Value\"")
            .SingleAsync(cancellationToken);

        var resultProcessor = new ProbeResultStatusProcessor(db, new TransactionUtcClock(expiryCutoffReceivedAt));
        while (await resultProcessor.ProcessNextInTransactionAsync(probeId, expiryCutoffReceivedAt, cancellationToken) is not null)
        {
        }

        var cause = await db.ProbeFreshnessExpiryCauses
            .Where(row => row.ProbeId == probeId && row.DueAt <= expiryCutoffReceivedAt &&
                !db.ProbeFreshnessExpiryCauseDispositions.Any(disposition => disposition.CauseId == row.CauseId))
            .OrderBy(row => row.DueAt)
            .ThenBy(row => row.RequestedAt)
            .ThenBy(row => row.CauseId)
            .FirstOrDefaultAsync(cancellationToken);

        if (cause is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ProbeFreshnessExpiryProcessorOutcomeKind.NoDueCause);
        }

        var projection = await db.ProbeStatusProjections
            .FromSqlInterpolated($"SELECT * FROM probe_status_projections WHERE probe_id = {probeId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        ProbeFreshnessExpiryCauseDisposition disposition;
        if (projection is null)
        {
            disposition = ProbeFreshnessExpiryCauseDisposition.NoOp(cause.CauseId, cause.ProbeId,
                cause.PolicySnapshotId, cause.PolicyVersion,
                ProbeFreshnessExpiryCauseDisposition.ProjectionMissingReasonCode, expiryCutoffReceivedAt);
        }
        else if (projection.WatermarkEventAt != cause.SourceCursorEventAt ||
                 projection.WatermarkAgentId != cause.SourceAgentId ||
                 projection.WatermarkResultId != cause.SourceResultId ||
                 projection.LastFreshEventAt != cause.SourceLastFreshEventAt)
        {
            disposition = ProbeFreshnessExpiryCauseDisposition.NoOp(cause.CauseId, cause.ProbeId,
                cause.PolicySnapshotId, cause.PolicyVersion,
                ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode, expiryCutoffReceivedAt);
        }
        else if (projection.VisibleStatus == ProbeStatus.Unknown)
        {
            disposition = ProbeFreshnessExpiryCauseDisposition.NoOp(cause.CauseId, cause.ProbeId,
                cause.PolicySnapshotId, cause.PolicyVersion,
                ProbeFreshnessExpiryCauseDisposition.VisibleAlreadyUnknownReasonCode, expiryCutoffReceivedAt);
        }
        else
        {
            var fromVisibleStatus = projection.VisibleStatus;
            projection.ExpireResultFreshness();
            disposition = ProbeFreshnessExpiryCauseDisposition.Applied(cause.CauseId, cause.ProbeId,
                cause.PolicySnapshotId, cause.PolicyVersion, expiryCutoffReceivedAt);
            db.Add(new ProbeFreshnessExpiryCauseTransition(cause.CauseId, cause.ProbeId,
                cause.PolicySnapshotId, cause.PolicyVersion, fromVisibleStatus, expiryCutoffReceivedAt));
        }

        db.Add(disposition);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return disposition.Outcome == ProbeFreshnessExpiryCauseDispositionOutcome.Applied
            ? new(ProbeFreshnessExpiryProcessorOutcomeKind.Applied, cause.CauseId, disposition.Outcome, disposition.ReasonCode)
            : new(ProbeFreshnessExpiryProcessorOutcomeKind.NoOp, cause.CauseId, disposition.Outcome, disposition.ReasonCode);
    }

    private sealed class TransactionUtcClock(DateTimeOffset now) : EePulse.Application.Time.IUtcClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
