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

        // Capture the complete required Agent set before the Probe lock.  If a result or
        // cause appears while acquiring it, roll back and retry with a fresh transaction.
        while (true)
        {
            var agents = await RequiredAgentIdsAsync(probeId, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                var lockedAgents = new Dictionary<Guid, EePulse.Domain.Agents.Agent>(agents.Length);
                foreach (var agentId in agents)
                {
                    var agent = await db.Agents.FromSqlInterpolated($"SELECT * FROM agents WHERE id = {agentId} FOR SHARE").SingleAsync(cancellationToken);
                    if (agent.Id != agentId || !lockedAgents.TryAdd(agentId, agent)) throw new InvalidOperationException("The freshness expiry processor did not lock its required Agent set.");
                }
                if (lockedAgents.Count != agents.Length) throw new InvalidOperationException("The freshness expiry processor did not lock its required Agent set.");
                await ProbeTransactionLock.AcquireAsync(db, probeId, cancellationToken);
                var recheckedAgents = await RequiredAgentIdsAsync(probeId, cancellationToken);
                if (!agents.SequenceEqual(recheckedAgents)) { await transaction.RollbackAsync(cancellationToken); db.ChangeTracker.Clear(); continue; }

                var expiryCutoffReceivedAt = await db.Database
                    .SqlQueryRaw<DateTimeOffset>("SELECT date_trunc('microseconds', clock_timestamp()) AS \"Value\"")
                    .SingleAsync(cancellationToken);

                var resultProcessor = new ProbeResultStatusProcessor(db, new TransactionUtcClock(expiryCutoffReceivedAt));
                while (true)
                {
                    var next = await resultProcessor.NextLedgerAsync(probeId, expiryCutoffReceivedAt, cancellationToken);
                    if (next is null) break;
                    if (!lockedAgents.TryGetValue(next.AgentId, out var sourceAgent))
                        throw new InvalidOperationException("The freshness expiry processor requires every source Agent to be prelocked.");
                    await resultProcessor.ProcessNextInTransactionAsync(probeId, expiryCutoffReceivedAt, sourceAgent, cancellationToken);
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
                if (disposition.Outcome == ProbeFreshnessExpiryCauseDispositionOutcome.Applied)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET state_version = state_version + 1 WHERE probe_id = {probeId}", cancellationToken);
                    var version = db.Entry(projection!).Property(row => row.StateVersion);
                    version.CurrentValue++;
                    version.OriginalValue = version.CurrentValue;
                    version.IsModified = false;
                }
                await transaction.CommitAsync(cancellationToken);
                return disposition.Outcome == ProbeFreshnessExpiryCauseDispositionOutcome.Applied
                    ? new(ProbeFreshnessExpiryProcessorOutcomeKind.Applied, cause.CauseId, disposition.Outcome, disposition.ReasonCode)
                    : new(ProbeFreshnessExpiryProcessorOutcomeKind.NoOp, cause.CauseId, disposition.Outcome, disposition.ReasonCode);
            }
            catch { if (db.Database.CurrentTransaction is not null) await transaction.RollbackAsync(CancellationToken.None); throw; }
        }
    }

    private async Task<Guid[]> RequiredAgentIdsAsync(Guid probeId, CancellationToken ct) =>
        (await db.ProbeResultLedgerEntries.AsNoTracking().Where(row => row.ProbeId == probeId && !db.ProbeResultProcessingDispositions.Any(d => d.AgentId == row.AgentId && d.ResultId == row.ResultId)).Select(row => row.AgentId)
            .Concat(db.ProbeFreshnessExpiryCauses.AsNoTracking().Where(row => row.ProbeId == probeId && !db.ProbeFreshnessExpiryCauseDispositions.Any(d => d.CauseId == row.CauseId)).Select(row => row.SourceAgentId)).ToArrayAsync(ct))
        .Distinct().OrderBy(x => x.ToString("D"), StringComparer.Ordinal).ToArray();

    private sealed class TransactionUtcClock(DateTimeOffset now) : EePulse.Application.Time.IUtcClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
