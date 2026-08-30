using EePulse.Domain.Agents;
using EePulse.Domain.Status;
using Microsoft.EntityFrameworkCore;

namespace EePulse.Infrastructure.Persistence.ProbeProcessing;

public enum ProbeHeartbeatExpiryProcessorOutcomeKind { NoDueCause, Applied, NoOp }

public sealed record ProbeHeartbeatExpiryProcessorOutcome(
    ProbeHeartbeatExpiryProcessorOutcomeKind Kind, Guid? CauseId = null,
    ProbeHeartbeatExpiryCauseDispositionOutcome? DispositionOutcome = null, string? ReasonCode = null);

/// <summary>Serializes one due heartbeat cause with its authority Agent and Probe.</summary>
public sealed class ProbeHeartbeatExpiryCauseProcessor(EePulseDbContext db)
{
    public async Task<ProbeHeartbeatExpiryProcessorOutcome> ProcessNextDueAsync(Guid probeId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(probeId, Guid.Empty);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var agentIds = await RequiredAgentIdsAsync(probeId, cancellationToken);

            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                var lockedAgents = new Dictionary<Guid, Agent>(agentIds.Length);
                foreach (var agentId in agentIds)
                {
                    var agent = await db.Agents.FromSqlInterpolated($"SELECT * FROM agents WHERE id = {agentId} FOR SHARE").SingleAsync(cancellationToken);
                    if (agent.Id != agentId || !lockedAgents.TryAdd(agentId, agent)) throw new InvalidOperationException("The heartbeat expiry processor did not lock its required Agent set.");
                }
                if (lockedAgents.Count != agentIds.Length) throw new InvalidOperationException("The heartbeat expiry processor did not lock its required Agent set.");
                await ProbeTransactionLock.AcquireAsync(db, probeId, cancellationToken);
                var recheckedAgentIds = await RequiredAgentIdsAsync(probeId, cancellationToken);
                if (!agentIds.SequenceEqual(recheckedAgentIds))
                { await transaction.RollbackAsync(cancellationToken); db.ChangeTracker.Clear(); continue; }

                var cutoff = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT date_trunc('microseconds', clock_timestamp()) AS \"Value\"").SingleAsync(cancellationToken);
                var resultProcessor = new ProbeResultStatusProcessor(db, new TransactionUtcClock(cutoff));
                while (true) { var next = await resultProcessor.NextLedgerAsync(probeId, cutoff, cancellationToken); if (next is null) break; if (!lockedAgents.TryGetValue(next.AgentId, out var sourceAgent)) throw new InvalidOperationException("The heartbeat expiry processor requires every source Agent to be prelocked."); await resultProcessor.ProcessNextInTransactionAsync(probeId, cutoff, sourceAgent, cancellationToken); }
                var candidate = await db.ProbeHeartbeatExpiryCauses.Where(x => x.ProbeId == probeId && x.DueAt <= cutoff && !db.ProbeHeartbeatExpiryCauseDispositions.Any(d => d.CauseId == x.CauseId)).OrderBy(x => x.DueAt).ThenBy(x => x.RequestedAt).ThenBy(x => x.CauseId).FirstOrDefaultAsync(cancellationToken);
                if (candidate is null) { await transaction.CommitAsync(cancellationToken); return new(ProbeHeartbeatExpiryProcessorOutcomeKind.NoDueCause); }
                if (!lockedAgents.TryGetValue(candidate.AuthorityAgentId, out var authority)) throw new InvalidOperationException("The heartbeat expiry processor requires its authority Agent to be prelocked.");
                var projection = await db.ProbeStatusProjections.FromSqlInterpolated($"SELECT * FROM probe_status_projections WHERE probe_id = {probeId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
                ProbeHeartbeatExpiryCauseDisposition disposition;
                if (projection is null) disposition = ProbeHeartbeatExpiryCauseDisposition.ProjectionMissing(candidate.CauseId, probeId, candidate.PolicySnapshotId, candidate.PolicyVersion, cutoff);
                else if (projection.WatermarkAgentId != candidate.AuthorityAgentId || projection.WatermarkResultId != candidate.SourceResultId || projection.WatermarkEventAt != candidate.SourceCursorEventAt) disposition = ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSuperseded(candidate.CauseId, probeId, candidate.PolicySnapshotId, candidate.PolicyVersion, cutoff);
                else if (authority.LastHeartbeatAt != candidate.SourceLastHeartbeatReceivedAt || authority.HeartbeatIntervalSeconds != candidate.SourceHeartbeatIntervalSeconds) disposition = ProbeHeartbeatExpiryCauseDisposition.AuthorityHeartbeatAdvanced(candidate.CauseId, probeId, candidate.PolicySnapshotId, candidate.PolicyVersion, cutoff);
                else if (projection.VisibleStatus == ProbeStatus.Unknown) disposition = ProbeHeartbeatExpiryCauseDisposition.VisibleAlreadyUnknown(candidate.CauseId, probeId, candidate.PolicySnapshotId, candidate.PolicyVersion, cutoff);
                else
                {
                    var from = projection.VisibleStatus;
                    projection.ExpireResultFreshness();
                    disposition = ProbeHeartbeatExpiryCauseDisposition.Applied(candidate.CauseId, probeId, candidate.PolicySnapshotId, candidate.PolicyVersion, cutoff);
                    db.Add(new ProbeHeartbeatExpiryCauseTransition(candidate.CauseId, probeId, candidate.PolicySnapshotId, candidate.PolicyVersion, from, cutoff));
                }
                db.Add(disposition);
                await db.SaveChangesAsync(cancellationToken);
                if (disposition.Outcome == ProbeHeartbeatExpiryCauseDispositionOutcome.Applied)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET state_version = state_version + 1 WHERE probe_id = {probeId}", cancellationToken);
                    var version = db.Entry(projection!).Property(row => row.StateVersion);
                    version.CurrentValue++;
                    version.OriginalValue = version.CurrentValue;
                    version.IsModified = false;
                }
                await transaction.CommitAsync(cancellationToken);
                return disposition.Outcome == ProbeHeartbeatExpiryCauseDispositionOutcome.Applied
                    ? new(ProbeHeartbeatExpiryProcessorOutcomeKind.Applied, candidate.CauseId, disposition.Outcome, disposition.ReasonCode)
                    : new(ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, candidate.CauseId, disposition.Outcome, disposition.ReasonCode);
            }
            catch { if (db.Database.CurrentTransaction is not null) await transaction.RollbackAsync(CancellationToken.None); throw; }
        }
    }

    private async Task<Guid[]> RequiredAgentIdsAsync(Guid probeId, CancellationToken ct) =>
        (await db.ProbeResultLedgerEntries.AsNoTracking().Where(x => x.ProbeId == probeId && !db.ProbeResultProcessingDispositions.Any(d => d.AgentId == x.AgentId && d.ResultId == x.ResultId)).Select(x => x.AgentId)
            .Concat(db.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == probeId && !db.ProbeHeartbeatExpiryCauseDispositions.Any(d => d.CauseId == x.CauseId)).Select(x => x.AuthorityAgentId)).ToArrayAsync(ct))
        .Distinct().OrderBy(x => x.ToString("D"), StringComparer.Ordinal).ToArray();
}

internal sealed class TransactionUtcClock(DateTimeOffset now) : EePulse.Application.Time.IUtcClock { public DateTimeOffset UtcNow => now; }
