using EePulse.Application.Time;
using EePulse.Domain.Agents;
using EePulse.Domain.Status;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

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

        var outcome = await ProcessNextInTransactionAsync(probeId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome ?? new(ProbeResultStatusProcessorOutcomeKind.NoPending);
    }

    // The freshness-expiry worker shares this transaction-local path so its cutoff is
    // evaluated after every result that was already received at that cutoff.
    internal async Task<ProbeResultStatusProcessorOutcome?> ProcessNextInTransactionAsync(
        Guid probeId,
        DateTimeOffset? receivedAtCutoff,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An active EePulseDbContext database transaction is required to process a Probe result.");

        var ledger = await db.ProbeResultLedgerEntries
            .Where(row => row.ProbeId == probeId && !db.ProbeResultProcessingDispositions
                .Any(disposition => disposition.AgentId == row.AgentId && disposition.ResultId == row.ResultId) &&
                (!receivedAtCutoff.HasValue || row.ReceivedAt <= receivedAtCutoff.Value))
            .OrderBy(row => row.EndedAt)
            .ThenBy(row => row.AgentId)
            .ThenBy(row => row.ResultId)
            .FirstOrDefaultAsync(cancellationToken);

        if (ledger is null)
        {
            return null;
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
        var schedulingOwnership = await (
            from probe in db.Probes
            join device in db.Devices on probe.DeviceId equals device.Id
            join agentGroup in db.AgentGroups on probe.AgentGroupId equals agentGroup.Id
            where probe.Id == ledger.ProbeId
            select new { probe.Enabled, DeviceEnabled = device.Enabled, AgentGroupEnabled = agentGroup.Enabled })
            .SingleAsync(cancellationToken);

        var decidedAt = clock.UtcNow;
        var resolvedSnapshotId = snapshot?.Id;
        var resolvedPolicyVersion = snapshot?.PolicyVersion;
        var disposition = ResolveDisposition(
            ledger,
            projection,
            snapshot,
            boundary,
            !schedulingOwnership.Enabled || !schedulingOwnership.DeviceEnabled || !schedulingOwnership.AgentGroupEnabled);
        var addedProjection = false;
        ProbeStatusEvaluationResult? evaluation = null;
        FreshnessCauseInputs? freshnessCauseInputs = null;

        if (disposition.Kind == ProbeResultProcessingDispositionKind.StateDriving)
        {
            projection ??= new ProbeStatusProjection(probeId, ProbeStatus.Unknown, 0, 0, null, null, null, null);
            addedProjection = db.Entry(projection).State == EntityState.Detached;
            evaluation = ProbeStatusEvaluationKernel.Evaluate(
                new(snapshot!.FailureThreshold, snapshot.RecoveryThreshold, snapshot.WarningRttMilliseconds, snapshot.WarningPacketLossRatio),
                new(projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount),
                new(ledger.SuccessfulAttemptCount == ledger.AttemptCount, ledger.AverageRttMilliseconds, ledger.PacketLossRatio));
            projection.ApplyResult(evaluation.State, ledger.EndedAt, ledger.AgentId, ledger.ResultId);
            if (addedProjection) db.Add(projection);
            freshnessCauseInputs = await ResolveFreshnessCauseInputsAsync(ledger, snapshot, projection, cancellationToken);
        }

        var processingDisposition = new ProbeResultProcessingDisposition(
            ledger.AgentId,
            ledger.ResultId,
            ledger.ProbeId,
            ledger.EndedAt,
            disposition.Kind,
            disposition.ReasonCode,
            resolvedSnapshotId,
            resolvedPolicyVersion,
            decidedAt);
        db.Add(processingDisposition);
        if (disposition.Kind == ProbeResultProcessingDispositionKind.StateDriving && evaluation?.Transition is { } transition)
        {
            var persistedTransition = new ProbeResultStatusTransition(
                ledger.AgentId,
                ledger.ResultId,
                ledger.ProbeId,
                transition.From,
                transition.To,
                ProbeResultStatusTransition.ReasonCodeFor(transition.Reason),
                ledger.EndedAt,
                ledger.ReceivedAt,
                disposition.Kind);
            db.Add(persistedTransition);

            if (IsAvailabilityDownOpening(transition))
            {
                var activeIncident = await db.AvailabilityIncidents.SingleOrDefaultAsync(incident =>
                    incident.ProbeId == ledger.ProbeId &&
                    (incident.Status == AvailabilityIncidentStatus.Open || incident.Status == AvailabilityIncidentStatus.Acknowledged),
                    cancellationToken);
                var openIncidentId = projection!.OpenIncidentId;

                if (openIncidentId.HasValue && (activeIncident is null || activeIncident.Id != openIncidentId.Value))
                {
                    throw new InvalidOperationException("The Probe status projection references an inconsistent active availability incident.");
                }

                if (activeIncident is not null)
                {
                    if (!openIncidentId.HasValue)
                    {
                        db.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = activeIncident.Id;
                    }
                }
                else
                {
                    var incident = new AvailabilityIncident(Guid.NewGuid(), ledger.ProbeId, ledger.EndedAt);
                    var lifecycleEvent = new IncidentLifecycleEvent(Guid.NewGuid(), incident.Id, ledger.ProbeId,
                        ledger.AgentId, ledger.ResultId, transition.From, snapshot!.Id, snapshot.PolicyVersion, ledger.EndedAt);
                    var suppressionContext = NotificationSuppressionContext.ForAvailabilityDownOpened(lifecycleEvent, ledger.ReceivedAt);

                    db.AddRange(incident, lifecycleEvent, suppressionContext);
                    db.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = incident.Id;
                }
            }
            else if (IsConfirmedRecovery(transition))
            {
                var activeIncident = await db.AvailabilityIncidents.SingleOrDefaultAsync(incident =>
                    incident.ProbeId == ledger.ProbeId &&
                    (incident.Status == AvailabilityIncidentStatus.Open || incident.Status == AvailabilityIncidentStatus.Acknowledged),
                    cancellationToken);
                var openIncidentId = projection!.OpenIncidentId;

                if (openIncidentId.HasValue && (activeIncident is null || activeIncident.Id != openIncidentId.Value))
                {
                    throw new InvalidOperationException("The Probe status projection references an inconsistent active availability incident.");
                }

                if (activeIncident is not null)
                {
                    activeIncident.ResolveForConfirmedRecovery(ledger.EndedAt);
                    var lifecycleEvent = IncidentLifecycleEvent.ForConfirmedRecovery(Guid.NewGuid(), activeIncident.Id,
                        ledger.ProbeId, ledger.AgentId, ledger.ResultId, transition.To, snapshot!.Id, snapshot.PolicyVersion, ledger.EndedAt);
                    var suppressionContext = NotificationSuppressionContext.ForConfirmedRecovery(lifecycleEvent, ledger.ReceivedAt);

                    db.AddRange(lifecycleEvent, suppressionContext);
                    db.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = null;
                }
            }
            else if (IsRecoveryFailedOccurrence(transition))
            {
                var activeIncident = await db.AvailabilityIncidents.SingleOrDefaultAsync(incident =>
                    incident.ProbeId == ledger.ProbeId &&
                    (incident.Status == AvailabilityIncidentStatus.Open || incident.Status == AvailabilityIncidentStatus.Acknowledged),
                    cancellationToken);
                var openIncidentId = projection!.OpenIncidentId;

                if (!openIncidentId.HasValue || activeIncident is null || activeIncident.Id != openIncidentId.Value)
                {
                    throw new InvalidOperationException("A recovery-failed transition requires the Probe status projection to reference its active availability incident.");
                }

                activeIncident.RecordRecoveryFailedOccurrence();
                var lifecycleEvent = IncidentLifecycleEvent.ForRecoveryFailedOccurrence(Guid.NewGuid(), activeIncident.Id,
                    ledger.ProbeId, ledger.AgentId, ledger.ResultId, snapshot!.Id, snapshot.PolicyVersion, ledger.EndedAt);
                var suppressionContext = NotificationSuppressionContext.ForSuppressedRecoveryFailed(lifecycleEvent, ledger.ReceivedAt);

                db.AddRange(lifecycleEvent, suppressionContext);
            }
        }
        // This first flush is deliberately still inside the transaction.  The freshness-cause
        // trigger must be able to read this result's persisted disposition and projection.
        await db.SaveChangesAsync(cancellationToken);

        if (addedProjection)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE probe_status_projections SET state_version = state_version + 1 WHERE probe_id = {probeId}",
                cancellationToken);
            var stateVersion = db.Entry(projection!).Property(row => row.StateVersion);
            stateVersion.CurrentValue = 1;
            stateVersion.OriginalValue = 1;
            stateVersion.IsModified = false;
        }

        if (freshnessCauseInputs is not null)
        {
            db.Add(new ProbeFreshnessExpiryCause(
                Guid.NewGuid(),
                ledger.ProbeId,
                ledger.AgentId,
                ledger.ResultId,
                ledger.EndedAt,
                projection!.LastFreshEventAt!.Value,
                ledger.ConfigurationVersion,
                freshnessCauseInputs.AgentGroupId,
                snapshot!.Id,
                snapshot.PolicyVersion,
                freshnessCauseInputs.IntervalSeconds,
                freshnessCauseInputs.GraceSeconds,
                freshnessCauseInputs.DueAt));

            // Keep this separate from the result flush: ST-09B validates the source through
            // transactionally persisted rows, not EF's pending change tracker.
            await db.SaveChangesAsync(cancellationToken);
        }

        return new(ProbeResultStatusProcessorOutcomeKind.Processed, ledger.AgentId, ledger.ResultId, disposition.Kind);
    }

    private static DispositionDecision ResolveDisposition(
        ProbeResultLedgerEntry ledger,
        ProbeStatusProjection? projection,
        ProbeStatusPolicySnapshot? snapshot,
        AgentConfigurationEffectiveBoundary? boundary,
        bool schedulingOwnershipDisabled)
    {
        if (snapshot is null || boundary is null)
        {
            return new(ProbeResultProcessingDispositionKind.HistoricalOther, "policy-lineage-unresolved");
        }

        if (ledger.ReceivedAt < boundary.AppliedAcknowledgementReceivedAt)
        {
            return new(ProbeResultProcessingDispositionKind.HistoricalOther, "config-not-effective");
        }

        if (schedulingOwnershipDisabled)
        {
            return new(ProbeResultProcessingDispositionKind.Disabled, "disabled");
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

    private static bool IsAvailabilityDownOpening(ProbeStatusTransition transition) =>
        transition.From != ProbeStatus.Down &&
        transition.To == ProbeStatus.Down &&
        transition.Reason == ProbeStatusTransitionReason.FailureThresholdMet;

    private static bool IsConfirmedRecovery(ProbeStatusTransition transition) =>
        transition.From == ProbeStatus.Recovering &&
        transition.To is ProbeStatus.Up or ProbeStatus.Degraded &&
        transition.Reason == ProbeStatusTransitionReason.RecoveryThresholdMet;

    private static bool IsRecoveryFailedOccurrence(ProbeStatusTransition transition) =>
        transition.From == ProbeStatus.Recovering &&
        transition.To == ProbeStatus.Down &&
        transition.Reason == ProbeStatusTransitionReason.RecoveryFailed;

    private async Task<FreshnessCauseInputs> ResolveFreshnessCauseInputsAsync(
        ProbeResultLedgerEntry ledger,
        ProbeStatusPolicySnapshot snapshot,
        ProbeStatusProjection projection,
        CancellationToken cancellationToken)
    {
        var agent = await db.Agents.SingleOrDefaultAsync(row => row.Id == ledger.AgentId, cancellationToken)
            ?? throw new InvalidOperationException("WP-06 freshness cause source Agent is missing.");
        var configuration = await db.AgentConfigurationSnapshots.SingleOrDefaultAsync(row =>
            row.AgentGroupId == agent.AgentGroupId && row.Version == ledger.ConfigurationVersion, cancellationToken)
            ?? throw new InvalidOperationException("WP-06 freshness cause source configuration snapshot is missing.");

        var intervalSeconds = ReadSourceIntervalSeconds(configuration.Payload, ledger.ProbeId);
        var lastFreshEventAt = projection.LastFreshEventAt
            ?? throw new InvalidOperationException("WP-06 freshness cause source projection has no last-fresh event.");
        var graceSeconds = Math.Max(60, checked(3 * agent.HeartbeatIntervalSeconds));
        var freshnessSeconds = Math.Max(checked(2 * intervalSeconds), graceSeconds);
        return new(agent.AgentGroupId, intervalSeconds, graceSeconds, lastFreshEventAt.AddSeconds(freshnessSeconds));
    }

    private static int ReadSourceIntervalSeconds(string payload, Guid probeId)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("probes", out var probes) || probes.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("WP-06 freshness cause source configuration payload has no probes array.");

            var canonicalProbeId = probeId.ToString("D");
            var matches = probes.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("probeId", out var id) && id.ValueKind == JsonValueKind.String &&
                    string.Equals(id.GetString(), canonicalProbeId, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("WP-06 freshness cause source configuration must contain exactly one matching Probe.");
            if (!matches[0].TryGetProperty("intervalSeconds", out var interval) || interval.ValueKind != JsonValueKind.Number)
                throw new InvalidOperationException("WP-06 freshness cause source intervalSeconds is invalid.");

            var raw = interval.GetRawText();
            if (raw.Length == 0 || raw.Any(character => character is < '0' or > '9') ||
                !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
                throw new InvalidOperationException("WP-06 freshness cause source intervalSeconds is invalid.");
            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("WP-06 freshness cause source configuration payload is malformed.", exception);
        }
    }

    private sealed record DispositionDecision(ProbeResultProcessingDispositionKind Kind, string ReasonCode);
    private sealed record FreshnessCauseInputs(Guid AgentGroupId, int IntervalSeconds, int GraceSeconds, DateTimeOffset DueAt);
}
