using EePulse.Application.Time;
using EePulse.Domain.Agents;
using EePulse.Domain.Status;
using Microsoft.EntityFrameworkCore;

namespace EePulse.Infrastructure.Persistence.ProbeProcessing;

public enum ProbeResultStatusProcessorOutcomeKind
{
    NoPending,
    Processed,
}

public sealed record ProbeResultStatusProcessorOutcome(
    ProbeResultStatusProcessorOutcomeKind Kind,
    Guid? AgentId = null,
    Guid? ResultId = null,
    ProbeResultProcessingDispositionKind? Disposition = null);

public sealed class ProbeResultStatusProcessor(EePulseDbContext db, IUtcClock clock)
{
    public async Task<ProbeResultStatusProcessorOutcome> ProcessNextAsync(
        Guid probeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(probeId, Guid.Empty);

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await ProbeTransactionLock.AcquireAsync(db, probeId, cancellationToken);

        var ledger = await db.ProbeResultLedgerEntries
            .Where(row => row.ProbeId == probeId && !db.ProbeResultProcessingDispositions
                .Any(disposition => disposition.AgentId == row.AgentId && disposition.ResultId == row.ResultId))
            .OrderBy(row => row.EndedAt)
            .ThenBy(row => row.AgentId)
            .ThenBy(row => row.ResultId)
            .FirstOrDefaultAsync(cancellationToken);

        if (ledger is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ProbeResultStatusProcessorOutcomeKind.NoPending);
        }

        var projection = await db.ProbeStatusProjections
            .FromSqlInterpolated($"SELECT * FROM probe_status_projections WHERE probe_id = {probeId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

        var binding = await db.ProbeStatusPolicyBindings
            .SingleOrDefaultAsync(row => row.ProbeId == ledger.ProbeId && row.ConfigurationVersion == ledger.ConfigurationVersion, cancellationToken);
        var snapshot = binding is null
            ? null
            : await db.ProbeStatusPolicySnapshots.SingleOrDefaultAsync(row => row.Id == binding.PolicySnapshotId, cancellationToken);
        var boundary = await db.AgentConfigurationEffectiveBoundaries
            .SingleOrDefaultAsync(row => row.AgentId == ledger.AgentId && row.ConfigurationVersion == ledger.ConfigurationVersion, cancellationToken);

        var decidedAt = clock.UtcNow;
        var resolvedSnapshotId = snapshot?.Id;
        var resolvedPolicyVersion = snapshot?.PolicyVersion;
        var disposition = ResolveDisposition(ledger, projection, snapshot, boundary);
        var addedProjection = false;

        if (disposition.Kind == ProbeResultProcessingDispositionKind.StateDriving)
        {
            projection ??= new ProbeStatusProjection(probeId, ProbeStatus.Unknown, 0, 0, null, null, null, null);
            addedProjection = db.Entry(projection).State == EntityState.Detached;
            var evaluation = ProbeStatusEvaluationKernel.Evaluate(
                new(snapshot!.FailureThreshold, snapshot.RecoveryThreshold, snapshot.WarningRttMilliseconds, snapshot.WarningPacketLossRatio),
                new(projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount),
                new(ledger.SuccessfulAttemptCount == ledger.AttemptCount, ledger.AverageRttMilliseconds, ledger.PacketLossRatio));
            projection.ApplyResult(evaluation.State, ledger.EndedAt, ledger.AgentId, ledger.ResultId);
            if (addedProjection) db.Add(projection);
        }

        db.Add(new ProbeResultProcessingDisposition(
            ledger.AgentId,
            ledger.ResultId,
            ledger.ProbeId,
            ledger.EndedAt,
            disposition.Kind,
            disposition.ReasonCode,
            resolvedSnapshotId,
            resolvedPolicyVersion,
            decidedAt));
        await db.SaveChangesAsync(cancellationToken);

        if (addedProjection)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE probe_status_projections SET state_version = state_version + 1 WHERE probe_id = {probeId}",
                cancellationToken);
            var stateVersion = db.Entry(projection!).Property(row => row.StateVersion);
            stateVersion.CurrentValue = 1;
            stateVersion.OriginalValue = 1;
        }

        await transaction.CommitAsync(cancellationToken);
        return new(ProbeResultStatusProcessorOutcomeKind.Processed, ledger.AgentId, ledger.ResultId, disposition.Kind);
    }

    private static DispositionDecision ResolveDisposition(
        ProbeResultLedgerEntry ledger,
        ProbeStatusProjection? projection,
        ProbeStatusPolicySnapshot? snapshot,
        AgentConfigurationEffectiveBoundary? boundary)
    {
        if (snapshot is null || boundary is null)
        {
            return new(ProbeResultProcessingDispositionKind.HistoricalOther, "policy-lineage-unresolved");
        }

        if (ledger.ReceivedAt < boundary.AppliedAcknowledgementReceivedAt)
        {
            return new(ProbeResultProcessingDispositionKind.HistoricalOther, "config-not-effective");
        }

        if (projection is not null && IsCursorLower(projection, ledger))
        {
            return new(ProbeResultProcessingDispositionKind.LateOrder, "late-order");
        }

        if (ledger.EndedAt < ledger.ReceivedAt.AddSeconds(-snapshot.ApprovedLatenessSeconds))
        {
            return new(ProbeResultProcessingDispositionKind.BeyondApprovedLateness, "beyond-approved-lateness");
        }

        if (ledger.EndedAt > ledger.ReceivedAt.AddSeconds(snapshot.ApprovedFutureSkewSeconds))
        {
            return new(ProbeResultProcessingDispositionKind.FutureOrSkewSuspect, "future-or-skew-suspect");
        }

        return new(ProbeResultProcessingDispositionKind.StateDriving, "state-driving");
    }

    private static bool IsCursorLower(ProbeStatusProjection projection, ProbeResultLedgerEntry ledger)
    {
        if (!projection.WatermarkEventAt.HasValue) return false;

        var eventComparison = ledger.EndedAt.CompareTo(projection.WatermarkEventAt.Value);
        if (eventComparison != 0) return eventComparison < 0;
        var agentComparison = string.CompareOrdinal(ledger.AgentId.ToString("D"), projection.WatermarkAgentId!.Value.ToString("D"));
        if (agentComparison != 0) return agentComparison < 0;
        return string.CompareOrdinal(ledger.ResultId.ToString("D"), projection.WatermarkResultId!.Value.ToString("D")) < 0;
    }

    private sealed record DispositionDecision(ProbeResultProcessingDispositionKind Kind, string ReasonCode);
}
