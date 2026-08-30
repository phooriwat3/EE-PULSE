using EePulse.Application.Time;
using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using System.Collections.Immutable;
using System.Data.Common;

namespace EePulse.IntegrationTests;

public sealed class ProbeResultStatusProcessorTests
{
    public static TheoryData<ProbeStatus, int, int, long> H1KnownVisibleStatuses => new()
    {
        { ProbeStatus.Up, 0, 1, 1 },
        { ProbeStatus.Degraded, 0, 1, 1 },
        { ProbeStatus.Down, 2, 0, 2 },
        { ProbeStatus.Recovering, 0, 1, 3 },
    };
    public static TheoryData<string, string> H1NamedNoOps => new()
    {
        { "ProjectionMissing", ProbeHeartbeatExpiryCauseDisposition.ProjectionMissingReasonCode },
        { "AuthorityWatermarkSuperseded", ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode },
        { "AuthorityHeartbeatAdvanced", ProbeHeartbeatExpiryCauseDisposition.AuthorityHeartbeatAdvancedReasonCode },
        { "VisibleAlreadyUnknown", ProbeHeartbeatExpiryCauseDisposition.VisibleAlreadyUnknownReasonCode },
    };

    public static TheoryData<bool, decimal, ProbeStatus> ConfirmedRecoveryVariants
    {
        get
        {
            var variants = new TheoryData<bool, decimal, ProbeStatus>();
            variants.Add(true, 1m, ProbeStatus.Up);
            variants.Add(false, 500m, ProbeStatus.Degraded);
            return variants;
        }
    }

    [Fact]
    public async Task T4B3ResultProcessorOwnsProbeBeforeH1AndSupersedesOldHeartbeatCause()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateFixtureAsync();
        var oldHeartbeat = (await ReadPostgresTimestampAsync(fixture.ConnectionString, ct)).AddMinutes(-4);
        var oldResult = Guid.Parse("b3000000-0000-0000-0000-000000000001"); var oldEvent = oldHeartbeat.AddSeconds(10);
        await SetHeartbeatAsync(fixture, fixture.AgentId, oldHeartbeat);
        await AddLedgerAsync(fixture, oldEvent, oldEvent, 3, 0m, resultId: oldResult);
        var oldCauseCreatedBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, ct);
        await using (var initial = new EePulseDbContext(fixture.Options)) Assert.Equal(oldResult, (await new ProbeResultStatusProcessor(initial, new FixedClock(oldEvent)).ProcessNextAsync(fixture.ProbeId, ct)).ResultId);
        var oldCauseCreatedAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, ct);
        var newResult = Guid.Parse("b3000000-0000-0000-0000-000000000002"); var newEvent = oldEvent.AddSeconds(1);
        await AddLedgerAsync(fixture, newEvent, newEvent, 3, 0m, resultId: newResult);
        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, ct); ProbeHeartbeatExpiryCauseSnapshot originalCause; ProbeStatusProjectionSnapshot projectionBefore; H1NoMutationSnapshot baseline;
        await using (var pre = new EePulseDbContext(fixture.Options))
        {
            var agent = await pre.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, ct);
            var acknowledgement = await pre.AgentConfigurationAcknowledgements.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ConfigurationVersion == 1, ct);
            var boundary = await pre.AgentConfigurationEffectiveBoundaries.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ConfigurationVersion == 1, ct);
            var configuration = await pre.AgentConfigurationSnapshots.AsNoTracking().SingleAsync(x => x.AgentGroupId == fixture.GroupId && x.Version == 1, ct);
            var binding = await pre.ProbeStatusPolicyBindings.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId && x.ConfigurationVersion == 1, ct);
            var policy = await pre.ProbeStatusPolicySnapshots.AsNoTracking().SingleAsync(x => x.Id == fixture.PolicyId && x.PolicyVersion == 1, ct);
            originalCause = await pre.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == oldResult).Select(x => new ProbeHeartbeatExpiryCauseSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).SingleAsync(ct);
            projectionBefore = await pre.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).Select(x => new ProbeStatusProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId, x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId)).SingleAsync(ct);
            var pending = await pre.ProbeResultLedgerEntries.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ResultId == newResult, ct);
            Assert.Equal((fixture.AgentId, fixture.GroupId, "processor", "processor", "1.0.0", AgentSelfHealth.Healthy, AgentStatus.Online, 0L, oldHeartbeat, oldHeartbeat, 20, 0L, 0L, fixture.Now, (DateTimeOffset?)null, (string?)null), (agent.Id, agent.AgentGroupId, agent.Name, agent.MachineName, agent.AgentVersion, agent.SelfHealth, agent.Status, agent.QueueDepth, agent.LastHeartbeatAt, agent.LastReportedAt, agent.HeartbeatIntervalSeconds, agent.DesiredConfigurationVersion, agent.LastAppliedConfigurationVersion, agent.CreatedAt, agent.RevokedAt, agent.RevocationReason));
            Assert.Equal((fixture.AgentId, 1L, acknowledgement.Id, AgentAcknowledgementStatus.Applied, fixture.Now), (boundary.AgentId, boundary.ConfigurationVersion, boundary.SourceAcknowledgementId, boundary.SourceAcknowledgementStatus, boundary.AppliedAcknowledgementReceivedAt));
            Assert.Equal((fixture.AgentId, 1L, AgentAcknowledgementStatus.Applied, fixture.Now, fixture.Now, fixture.Now, 1L, 1L), (acknowledgement.AgentId, acknowledgement.ConfigurationVersion, acknowledgement.Status, acknowledgement.AppliedAt, acknowledgement.SentAt, acknowledgement.ReceivedAt, acknowledgement.CentralEffectiveConfigurationVersion, acknowledgement.DesiredConfigurationVersion));
            Assert.Equal((fixture.GroupId, 1L, FreshnessPayload(fixture.ProbeId, 30), Convert.ToHexString(new byte[32]), fixture.Now, (long?)null), (configuration.AgentGroupId, configuration.Version, configuration.Payload, Convert.ToHexString(configuration.PayloadDigest), configuration.GeneratedAt, configuration.RollbackOfVersion));
            Assert.Equal((fixture.PolicyId, 1, 2, 2, (int?)500, (decimal?)null, 300, 60, fixture.Now), (policy.Id, policy.PolicyVersion, policy.FailureThreshold, policy.RecoveryThreshold, policy.WarningRttMilliseconds, policy.WarningPacketLossRatio, policy.ApprovedLatenessSeconds, policy.ApprovedFutureSkewSeconds, policy.CreatedAt));
            Assert.Equal((fixture.ProbeId, 1L, fixture.GroupId, fixture.PolicyId), (binding.ProbeId, binding.ConfigurationVersion, binding.AgentGroupId, binding.PolicySnapshotId));
            Assert.Equal((fixture.AgentId, newResult, fixture.ProbeId, 1L, newEvent.AddSeconds(-1), newEvent, 3, 3, 0m, 1m, 1m, 1m, (string?)null, Convert.ToHexString(new byte[32]), newEvent), (pending.AgentId, pending.ResultId, pending.ProbeId, pending.ConfigurationVersion, pending.StartedAt, pending.EndedAt, pending.AttemptCount, pending.SuccessfulAttemptCount, pending.PacketLossRatio, pending.MinRttMilliseconds, pending.AverageRttMilliseconds, pending.MaxRttMilliseconds, pending.ErrorCategory, Convert.ToHexString(pending.ImmutablePayloadDigest), pending.ReceivedAt));
            Assert.True(pending.ReceivedAt >= boundary.AppliedAcknowledgementReceivedAt);
            Assert.Equal(new ProbeStatusProjectionSnapshot(fixture.ProbeId, ProbeStatus.Up, ProbeStatus.Up, 0, 1, 1, fixture.AgentId, oldResult, oldEvent, oldEvent, null), projectionBefore);
            Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, oldResult, oldEvent, oldHeartbeat, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, oldHeartbeat.AddSeconds(60)), (originalCause.CauseType, originalCause.SourceDisposition, originalCause.ProbeId, originalCause.AuthorityAgentId, originalCause.SourceResultId, originalCause.SourceCursorEventAt, originalCause.SourceLastHeartbeatAt, originalCause.SourceHeartbeatIntervalSeconds, originalCause.SourceConfigurationVersion, originalCause.SourceAgentGroupId, originalCause.PolicySnapshotId, originalCause.PolicyVersion, originalCause.DueAt));
            Assert.NotEqual(Guid.Empty, originalCause.CauseId); Assert.InRange(originalCause.RequestedAt, oldCauseCreatedBefore, oldCauseCreatedAfter); Assert.True(originalCause.DueAt <= before);
            Assert.Equal(new[] { new LedgerOrder(fixture.AgentId, newResult, newEvent, newEvent) }, await ReadPendingOrderAsync(fixture));
            Assert.Equal(1, await pre.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId && x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == oldResult && x.DueAt <= before, ct));
            Assert.False(await pre.ProbeResultProcessingDispositions.AsNoTracking().AnyAsync(x => x.AgentId == fixture.AgentId && x.ResultId == newResult, ct)); Assert.False(await pre.ProbeResultStatusTransitions.AsNoTracking().AnyAsync(x => x.AgentId == fixture.AgentId && x.ResultId == newResult, ct)); Assert.False(await pre.ProbeFreshnessExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceResultId == newResult, ct)); Assert.False(await pre.ProbeHeartbeatExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceResultId == newResult, ct));
        }
        baseline = await CaptureH1NoMutationSnapshotAsync(fixture);
        Assert.Equal(2, baseline.Ledger.Length); Assert.Single(baseline.ResultDispositions); Assert.Single(baseline.ResultTransitions); Assert.Single(baseline.FreshnessCauses); Assert.Single(baseline.HeartbeatCauses); Assert.Empty(baseline.Dispositions); Assert.Empty(baseline.Transitions); Assert.Empty(baseline.Artifacts.Incidents); Assert.Empty(baseline.Artifacts.Events); Assert.Empty(baseline.Artifacts.Contexts);

        var resultApp = $"t4b3-result-{Guid.NewGuid():N}"; var h1App = $"t4b3-h1-{Guid.NewGuid():N}";
        var resultOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = resultApp }.ConnectionString).Options;
        var h1Options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = h1App }.ConnectionString).Options;
        await using var blockerA = new NpgsqlConnection(fixture.ConnectionString); await blockerA.OpenAsync(ct); await using var txA = await blockerA.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct); var pidA = await LockProjectionForUpdateAsync(blockerA, txA, fixture.ProbeId, ct);
        await using var observer = new NpgsqlConnection(fixture.ConnectionString); await observer.OpenAsync(ct);
        var resultDb = new EePulseDbContext(resultOptions); var h1Db = new EePulseDbContext(h1Options); var resultCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); var h1Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ProbeResultStatusProcessorOutcome>? resultTask = null; Task<ProbeHeartbeatExpiryProcessorOutcome>? h1Task = null; ProbeResultStatusProcessorOutcome? resultOutcome = null; ProbeHeartbeatExpiryProcessorOutcome? h1Outcome = null; T4B3BackendIdentity? backendB = null; T4B3BackendIdentity? backendC = null; Exception? primary = null; var releasedA = false;
        try
        {
            resultTask = RunResultAsync(resultDb, fixture.ProbeId, newEvent, value => resultOutcome = value, resultCancellation.Token);
            backendB = await CaptureT4B3BackendIdentityAsync(observer, resultApp, resultTask, ct); Assert.NotEqual(pidA, backendB.Pid);
            await WaitForGrantedProbeAndProjectionWaitAsync(observer, backendB, pidA, fixture.ProbeId, resultTask, ct);
            await using (var blocked = new EePulseDbContext(fixture.Options)) { Assert.False(await blocked.ProbeResultProcessingDispositions.AsNoTracking().AnyAsync(x => x.AgentId == fixture.AgentId && x.ResultId == newResult, ct)); Assert.False(await blocked.ProbeHeartbeatExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceResultId == newResult, ct)); Assert.Equal(projectionBefore, await blocked.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).Select(x => new ProbeStatusProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId, x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId)).SingleAsync(ct)); }
            AssertH1NoMutationSnapshotEqual(baseline, await CaptureH1NoMutationSnapshotAsync(fixture));
            h1Task = RunHeartbeatAsync(h1Db, fixture.ProbeId, value => h1Outcome = value, h1Cancellation.Token);
            backendC = await CaptureT4B3BackendIdentityAsync(observer, h1App, h1Task, ct); Assert.NotEqual(pidA, backendC.Pid); Assert.NotEqual(backendB.Pid, backendC.Pid);
            await WaitForProbeAdvisoryBlockedByAsync(observer, backendC, backendB, pidA, fixture.ProbeId, h1Task, ct);
            await txA.RollbackAsync(ct); releasedA = true;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(10)); await resultTask.WaitAsync(bounded.Token); await h1Task.WaitAsync(bounded.Token);
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            var failures = new List<Exception>(); async Task Attempt(string name, Func<CancellationToken, Task> action) { try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await action(cleanup.Token); } catch (Exception exception) { failures.Add(new InvalidOperationException($"T4B3 cleanup failed while {name}.", exception)); } }
            if (!releasedA) await Attempt("releasing projection blocker A", async token => await txA.RollbackAsync(token));
            var resultTerminal = resultTask is not null && await SettleT4B3TaskAsync("result processor B", resultTask, resultCancellation, backendB, observer, failures); var h1Terminal = h1Task is not null && await SettleT4B3TaskAsync("H1 processor C", h1Task, h1Cancellation, backendC, observer, failures);
            if (resultTask is null) { await Attempt("disposing never-started result processor context", async _ => await resultDb.DisposeAsync()); await Attempt("disposing never-started result processor CTS", _ => { resultCancellation.Dispose(); return Task.CompletedTask; }); }
            else if (resultTerminal) { await Attempt("disposing result processor context", async _ => await resultDb.DisposeAsync()); await Attempt("disposing result processor CTS", _ => { resultCancellation.Dispose(); return Task.CompletedTask; }); }
            else await Attempt("transferring result processor B ownership", _ => { TransferT4B3Ownership("result processor B", resultTask, resultCancellation, backendB, [new("result processor context", async () => await resultDb.DisposeAsync())], failures, primary); return Task.CompletedTask; });
            if (h1Task is null) { await Attempt("disposing never-started H1 context", async _ => await h1Db.DisposeAsync()); await Attempt("disposing never-started H1 CTS", _ => { h1Cancellation.Dispose(); return Task.CompletedTask; }); }
            else if (h1Terminal) { await Attempt("disposing H1 context", async _ => await h1Db.DisposeAsync()); await Attempt("disposing H1 CTS", _ => { h1Cancellation.Dispose(); return Task.CompletedTask; }); }
            else await Attempt("transferring H1 processor C ownership", _ => { TransferT4B3Ownership("H1 processor C", h1Task, h1Cancellation, backendC, [new("H1 context", async () => await h1Db.DisposeAsync())], failures, primary); return Task.CompletedTask; });
            if (failures.Count > 0) { if (primary is not null) for (var index = 0; index < failures.Count; index++) primary.Data[$"T4B3CleanupFailure{index + 1}"] = failures[index]; else if (failures.Count == 1) throw failures[0]; else throw new AggregateException(failures); }
        }

        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, ct); await using var verify = new EePulseDbContext(fixture.Options);
        var newDisposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ResultId == newResult, ct); var fresh = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceResultId == newResult, ct); var successor = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceResultId == newResult, ct); var oldDisposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == originalCause.CauseId, ct); var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId, ct); var originalAfter = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.CauseId == originalCause.CauseId).Select(x => new ProbeHeartbeatExpiryCauseSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).SingleAsync(ct);
        Assert.Equal((ProbeResultStatusProcessorOutcomeKind.Processed, newResult), (resultOutcome!.Kind, resultOutcome.ResultId)); Assert.Equal((fixture.AgentId, newResult, fixture.ProbeId, newEvent, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1), (newDisposition.AgentId, newDisposition.ResultId, newDisposition.ProbeId, newDisposition.EventAt, newDisposition.Disposition, newDisposition.ReasonCode, newDisposition.ResolvedPolicySnapshotId, newDisposition.ResolvedPolicyVersion)); Assert.InRange(newDisposition.DecidedAt, before, after);
        Assert.False(await verify.ProbeResultStatusTransitions.AsNoTracking().AnyAsync(x => x.AgentId == fixture.AgentId && x.ResultId == newResult, ct));
        Assert.Equal((fixture.ProbeId, ProbeStatus.Up, ProbeStatus.Up, 0, 2, 2L, fixture.AgentId, newResult, newEvent, newEvent, (Guid?)null), (projection.ProbeId, projection.UnderlyingStatus, projection.VisibleStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.StateVersion, projection.WatermarkAgentId, projection.WatermarkResultId, projection.WatermarkEventAt, projection.LastFreshEventAt, projection.OpenIncidentId));
        Assert.NotEqual(Guid.Empty, fresh.CauseId); Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, newResult, newEvent, newEvent, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60, newEvent.AddSeconds(60)), (fresh.CauseType, fresh.SourceDisposition, fresh.ProbeId, fresh.SourceAgentId, fresh.SourceResultId, fresh.SourceCursorEventAt, fresh.SourceLastFreshEventAt, fresh.SourceConfigurationVersion, fresh.SourceAgentGroupId, fresh.PolicySnapshotId, fresh.PolicyVersion, fresh.FreshnessIntervalSeconds, fresh.FreshnessGraceSeconds, fresh.DueAt)); Assert.InRange(fresh.RequestedAt, before, after);
        Assert.Equal(1, await verify.ProbeFreshnessExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId && x.SourceAgentId == fixture.AgentId && x.SourceResultId == newResult && x.SourceCursorEventAt == newEvent && x.SourceLastFreshEventAt == newEvent, ct));
        Assert.NotEqual(Guid.Empty, successor.CauseId); Assert.NotEqual(originalCause.CauseId, successor.CauseId); Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, newResult, newEvent, oldHeartbeat, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, oldHeartbeat.AddSeconds(60)), (successor.CauseType, successor.SourceDisposition, successor.ProbeId, successor.AuthorityAgentId, successor.SourceResultId, successor.SourceCursorEventAt, successor.SourceLastHeartbeatReceivedAt, successor.SourceHeartbeatIntervalSeconds, successor.SourceConfigurationVersion, successor.SourceAgentGroupId, successor.PolicySnapshotId, successor.PolicyVersion, successor.DueAt)); Assert.InRange(successor.RequestedAt, before, after);
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId && x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == newResult && x.SourceCursorEventAt == newEvent && x.SourceLastHeartbeatReceivedAt == oldHeartbeat && x.SourceHeartbeatIntervalSeconds == 20, ct));
        Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, originalCause.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode), (h1Outcome!.Kind, h1Outcome.CauseId, h1Outcome.DispositionOutcome, h1Outcome.ReasonCode)); Assert.Equal((originalCause.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode, (DateTimeOffset?)null), (oldDisposition.CauseId, oldDisposition.ProbeId, oldDisposition.PolicySnapshotId, oldDisposition.PolicyVersion, oldDisposition.Outcome, oldDisposition.ReasonCode, oldDisposition.AppliedAt)); Assert.InRange(oldDisposition.ExpiryCutoffReceivedAt, before, after); Assert.Equal(originalCause, originalAfter); Assert.Empty(await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().Where(x => x.CauseId == originalCause.CauseId || x.CauseId == successor.CauseId).ToArrayAsync(ct)); Assert.False(await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == successor.CauseId, ct)); Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, ct));
        var final = await CaptureH1NoMutationSnapshotAsync(fixture);
        var expectedFinal = baseline with
        {
            Projection = new St10ProjectionSnapshot(fixture.ProbeId, ProbeStatus.Up, ProbeStatus.Up, 0, 2, newEvent, newEvent, fixture.AgentId, newResult, 2, null),
            ResultDispositions = baseline.ResultDispositions.Append(new St10ResultDispositionSnapshot(fixture.AgentId, newResult, fixture.ProbeId, newEvent, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1, newDisposition.DecidedAt)).OrderBy(x => x.AgentId).ThenBy(x => x.ResultId).ToArray(),
            FreshnessCauses = baseline.FreshnessCauses.Append(new FreshnessFullSnapshot(fresh.CauseId, fresh.ProbeId, fresh.CauseType, fresh.SourceAgentId, fresh.SourceResultId, fresh.SourceCursorEventAt, fresh.SourceLastFreshEventAt, fresh.SourceConfigurationVersion, fresh.SourceAgentGroupId, fresh.SourceDisposition, fresh.PolicySnapshotId, fresh.PolicyVersion, fresh.FreshnessIntervalSeconds, fresh.FreshnessGraceSeconds, fresh.DueAt, fresh.RequestedAt)).OrderBy(x => x.CauseId).ToArray(),
            HeartbeatCauses = baseline.HeartbeatCauses.Append(new HeartbeatFullSnapshot(successor.CauseId, successor.ProbeId, successor.CauseType, successor.AuthorityAgentId, successor.SourceResultId, successor.SourceCursorEventAt, successor.SourceLastHeartbeatReceivedAt, successor.SourceHeartbeatIntervalSeconds, successor.SourceConfigurationVersion, successor.SourceAgentGroupId, successor.SourceDisposition, successor.PolicySnapshotId, successor.PolicyVersion, successor.DueAt, successor.RequestedAt)).OrderBy(x => x.CauseId).ToArray(),
            Dispositions = baseline.Dispositions.Append(new HeartbeatDispositionFullSnapshot(oldDisposition.CauseId, oldDisposition.ProbeId, oldDisposition.PolicySnapshotId, oldDisposition.PolicyVersion, oldDisposition.Outcome, oldDisposition.ReasonCode, oldDisposition.ExpiryCutoffReceivedAt, oldDisposition.AppliedAt)).OrderBy(x => x.CauseId).ToArray()
        };
        AssertH1NoMutationSnapshotEqual(expectedFinal, final);
    }

    [Theory]
    [MemberData(nameof(ConfirmedRecoveryVariants))]
    public async Task St04ConfirmedRecoveryResolvesOpenOrAcknowledgedIncidentWithTheExpectedQuality(
        bool acknowledgeIncident,
        decimal averageRtt,
        ProbeStatus expectedStatus)
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await using (var opening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(opening, new FixedClock(fixture.Now));
            for (var index = 0; index < 3; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }
        if (acknowledgeIncident)
        {
            await using var acknowledge = new EePulseDbContext(fixture.Options);
            await acknowledge.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET status = {"Acknowledged"}, acknowledged_at = {fixture.Now.AddSeconds(2)}, acknowledged_by = {"operator"}, acknowledgement_comment = {"investigating"} WHERE probe_id = {fixture.ProbeId} AND status = {"Open"}", TestContext.Current.CancellationToken);
        }

        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m, averageRtt: averageRtt);
        var resolvedResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m, averageRtt: averageRtt);
        await using (var recovery = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(recovery, new FixedClock(fixture.Now));
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var transition = await verify.ProbeResultStatusTransitions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var lifecycleEvent = await verify.IncidentLifecycleEvents.SingleAsync(row => row.LifecycleEventType == IncidentLifecycleEventType.Resolved, TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.SingleAsync(row => row.EventId == lifecycleEvent.EventId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((ProbeStatus.Recovering, expectedStatus, "recovery-threshold-met"), (transition.FromStatus, transition.ToStatus, transition.ReasonCode));
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, disposition.Disposition);
        Assert.Equal(AvailabilityIncidentStatus.Resolved, incident.Status);
        Assert.Equal(fixture.Now.AddSeconds(4), incident.ResolvedAt);
        Assert.Equal(AvailabilityIncident.SystemPolicyActor, incident.ResolvedBy);
        Assert.Equal(AvailabilityIncident.ConfirmedRecoveryReason, incident.ResolutionNote);
        Assert.Null(projection.OpenIncidentId);
        Assert.Equal((incident.Id, fixture.ProbeId, fixture.AgentId, resolvedResultId, ProbeStatus.Recovering, expectedStatus, "recovery-threshold-met"),
            (lifecycleEvent.IncidentId, lifecycleEvent.ProbeId, lifecycleEvent.SourceAgentId, lifecycleEvent.SourceResultId, lifecycleEvent.SourceFromStatus, lifecycleEvent.SourceToStatus, lifecycleEvent.SourceReasonCode));
        Assert.Equal((disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion), (lifecycleEvent.PolicySnapshotId, lifecycleEvent.PolicyVersion));
        Assert.Equal((IncidentLifecycleEventType.Resolved, IncidentLifecycleEvent.ResolvedLifecycleEventKey), (lifecycleEvent.LifecycleEventType, lifecycleEvent.LifecycleEventKey));
        Assert.Equal((NotificationSuppressionEligibility.Eligible, AvailabilityIncident.ConfirmedRecoveryReason), (context.Eligibility, context.ReasonCode));
        Assert.Equal(lifecycleEvent.EventId, context.EventId);
    }

    [Fact]
    public async Task St04ConfirmedRecoveryAtomicallyResolvesTheActiveAvailabilityIncident()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        var recoveringResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m);
        var resolvedResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m);

        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using (var recovering = new EePulseDbContext(fixture.Options))
        {
            var recoveringProjection = await recovering.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ProbeStatus.Recovering, recoveringProjection.UnderlyingStatus);
            Assert.NotNull(recoveringProjection.OpenIncidentId);
            Assert.Single(await recovering.AvailabilityIncidents.Where(incident => incident.Status == AvailabilityIncidentStatus.Open).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Single(await recovering.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using (var processing = new EePulseDbContext(fixture.Options))
            Assert.Equal(resolvedResultId, (await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);

        await using var verify = new EePulseDbContext(fixture.Options);
        var transition = await verify.ProbeResultStatusTransitions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var lifecycleEvent = await verify.IncidentLifecycleEvents.SingleAsync(row => row.LifecycleEventType == IncidentLifecycleEventType.Resolved, TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.SingleAsync(row => row.EventId == lifecycleEvent.EventId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met"), (transition.FromStatus, transition.ToStatus, transition.ReasonCode));
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, disposition.Disposition);
        Assert.Equal(AvailabilityIncidentStatus.Resolved, incident.Status);
        Assert.Equal((fixture.Now.AddSeconds(4), AvailabilityIncident.SystemPolicyActor, AvailabilityIncident.ConfirmedRecoveryReason), (incident.ResolvedAt, incident.ResolvedBy, incident.ResolutionNote));
        Assert.Null(projection.OpenIncidentId);
        Assert.Equal((incident.Id, fixture.AgentId, resolvedResultId, ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met"),
            (lifecycleEvent.IncidentId, lifecycleEvent.SourceAgentId, lifecycleEvent.SourceResultId, lifecycleEvent.SourceFromStatus, lifecycleEvent.SourceToStatus, lifecycleEvent.SourceReasonCode));
        Assert.Equal((disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion), (lifecycleEvent.PolicySnapshotId, lifecycleEvent.PolicyVersion));
        Assert.Equal(IncidentLifecycleEventType.Resolved, lifecycleEvent.LifecycleEventType);
        Assert.Equal(IncidentLifecycleEvent.ResolvedLifecycleEventKey, lifecycleEvent.LifecycleEventKey);
        Assert.Equal((NotificationSuppressionEligibility.Eligible, AvailabilityIncident.ConfirmedRecoveryReason), (context.Eligibility, context.ReasonCode));
        Assert.Equal(recoveringResultId, (await verify.ProbeResultStatusTransitions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveringResultId, TestContext.Current.CancellationToken)).ResultId);
    }

    [Fact]
    public async Task St04ConfirmedRecoveryWithoutAnActiveIncidentCommitsOnlyNormalResultProcessing()
    {
        await using var fixture = await CreateFixtureAsync();
        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            seed.Add(new ProbeStatusProjection(fixture.ProbeId, ProbeStatus.Down, 2, 0, fixture.Now,
                fixture.Now, fixture.AgentId, Guid.NewGuid()));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);
        var resolvedResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 3, 0m);

        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var projection = await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProbeStatus.Up, projection.UnderlyingStatus);
        Assert.Null(projection.OpenIncidentId);
        Assert.NotNull(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken));
        Assert.NotNull(await verify.ProbeResultStatusTransitions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId && row.ReasonCode == "recovery-threshold-met", TestContext.Current.CancellationToken));
        Assert.Empty(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St04ConfirmedRecoveryWithAnInconsistentPointerRollsBackEveryRecoveryMutation()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m);
        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var mismatchedIncidentId = Guid.NewGuid();
        await using (var corrupt = new EePulseDbContext(fixture.Options))
        {
            await corrupt.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({mismatchedIncidentId}, {fixture.ProbeId}, {"availability-down"}, {"Resolved"}, {fixture.Now}, {fixture.Now}, {"system-policy"}, {"confirmed-recovery"})", TestContext.Current.CancellationToken);
            var projection = await corrupt.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            corrupt.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = mismatchedIncidentId;
            await corrupt.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var recoveryResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m);
        ProbeStatusProjection baselineProjection;
        (Guid AgentId, Guid ResultId)[] baselineDispositions;
        (Guid AgentId, Guid ResultId, ProbeStatus FromStatus, ProbeStatus ToStatus, string ReasonCode)[] baselineTransitions;
        (Guid Id, AvailabilityIncidentStatus Status, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? ResolutionNote)[] baselineIncidents;
        Guid[] baselineEventIds;
        Guid[] baselineContextIds;
        await using (var baseline = new EePulseDbContext(fixture.Options))
        {
            baselineProjection = await baseline.ProbeStatusProjections.AsNoTracking().SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            baselineDispositions = (await baseline.ProbeResultProcessingDispositions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId)).ToArray();
            baselineTransitions = (await baseline.ProbeResultStatusTransitions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId, row.FromStatus, row.ToStatus, row.ReasonCode)).ToArray();
            baselineIncidents = (await baseline.AvailabilityIncidents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.Id, row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote)).ToArray();
            baselineEventIds = await baseline.IncidentLifecycleEvents.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
            baselineContextIds = await baseline.NotificationSuppressionContexts.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        }

        await using (var processing = new EePulseDbContext(fixture.Options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using var verify = new EePulseDbContext(fixture.Options);
        var projectionAfterFailure = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(baselineProjection.UnderlyingStatus, projectionAfterFailure.UnderlyingStatus);
        Assert.Equal(baselineProjection.ConsecutiveFailureCount, projectionAfterFailure.ConsecutiveFailureCount);
        Assert.Equal(baselineProjection.ConsecutiveSuccessCount, projectionAfterFailure.ConsecutiveSuccessCount);
        Assert.Equal(baselineProjection.LastFreshEventAt, projectionAfterFailure.LastFreshEventAt);
        Assert.Equal(baselineProjection.WatermarkEventAt, projectionAfterFailure.WatermarkEventAt);
        Assert.Equal(baselineProjection.WatermarkAgentId, projectionAfterFailure.WatermarkAgentId);
        Assert.Equal(baselineProjection.WatermarkResultId, projectionAfterFailure.WatermarkResultId);
        Assert.Equal(baselineProjection.StateVersion, projectionAfterFailure.StateVersion);
        Assert.Equal(baselineProjection.OpenIncidentId, projectionAfterFailure.OpenIncidentId);
        Assert.Equal(baselineDispositions, (await verify.ProbeResultProcessingDispositions.OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId)).ToArray());
        Assert.Equal(baselineTransitions, (await verify.ProbeResultStatusTransitions.OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId, row.FromStatus, row.ToStatus, row.ReasonCode)).ToArray());
        Assert.Equal(baselineIncidents, (await verify.AvailabilityIncidents.OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.Id, row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote)).ToArray());
        Assert.Equal(baselineEventIds, await verify.IncidentLifecycleEvents.OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(baselineContextIds, await verify.NotificationSuppressionContexts.OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultStatusTransitions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St04ConfirmedRecoveryWithADanglingInactivePointerRollsBackEveryRecoveryMutation()
    {
        await using var fixture = await CreateFixtureAsync();
        var inactiveIncidentId = Guid.NewGuid();
        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({inactiveIncidentId}, {fixture.ProbeId}, {"availability-down"}, {"Resolved"}, {fixture.Now}, {fixture.Now}, {"system-policy"}, {"confirmed-recovery"})", TestContext.Current.CancellationToken);
            seed.Add(new ProbeStatusProjection(fixture.ProbeId, ProbeStatus.Recovering, 0, 1, fixture.Now,
                fixture.Now, fixture.AgentId, Guid.NewGuid(), inactiveIncidentId));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var recoveryResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);
        ProbeStatusProjection baselineProjection;
        (Guid AgentId, Guid ResultId)[] baselineDispositions;
        (Guid AgentId, Guid ResultId, ProbeStatus FromStatus, ProbeStatus ToStatus, string ReasonCode)[] baselineTransitions;
        (Guid Id, AvailabilityIncidentStatus Status, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? ResolutionNote)[] baselineIncidents;
        Guid[] baselineEventIds;
        Guid[] baselineContextIds;
        await using (var baseline = new EePulseDbContext(fixture.Options))
        {
            baselineProjection = await baseline.ProbeStatusProjections.AsNoTracking().SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            baselineDispositions = (await baseline.ProbeResultProcessingDispositions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId)).ToArray();
            baselineTransitions = (await baseline.ProbeResultStatusTransitions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId, row.FromStatus, row.ToStatus, row.ReasonCode)).ToArray();
            baselineIncidents = (await baseline.AvailabilityIncidents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.Id, row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote)).ToArray();
            baselineEventIds = await baseline.IncidentLifecycleEvents.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
            baselineContextIds = await baseline.NotificationSuppressionContexts.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        }

        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.Contains("inconsistent active availability incident", exception.Message, StringComparison.Ordinal);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var projectionAfterFailure = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(baselineProjection.UnderlyingStatus, projectionAfterFailure.UnderlyingStatus);
        Assert.Equal(baselineProjection.ConsecutiveFailureCount, projectionAfterFailure.ConsecutiveFailureCount);
        Assert.Equal(baselineProjection.ConsecutiveSuccessCount, projectionAfterFailure.ConsecutiveSuccessCount);
        Assert.Equal(baselineProjection.LastFreshEventAt, projectionAfterFailure.LastFreshEventAt);
        Assert.Equal(baselineProjection.WatermarkEventAt, projectionAfterFailure.WatermarkEventAt);
        Assert.Equal(baselineProjection.WatermarkAgentId, projectionAfterFailure.WatermarkAgentId);
        Assert.Equal(baselineProjection.WatermarkResultId, projectionAfterFailure.WatermarkResultId);
        Assert.Equal(baselineProjection.StateVersion, projectionAfterFailure.StateVersion);
        Assert.Equal(baselineProjection.OpenIncidentId, projectionAfterFailure.OpenIncidentId);
        Assert.Equal(baselineDispositions, (await verify.ProbeResultProcessingDispositions.OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId)).ToArray());
        Assert.Equal(baselineTransitions, (await verify.ProbeResultStatusTransitions.OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId, row.FromStatus, row.ToStatus, row.ReasonCode)).ToArray());
        Assert.Equal(baselineIncidents, (await verify.AvailabilityIncidents.OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.Id, row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote)).ToArray());
        Assert.Equal(baselineEventIds, await verify.IncidentLifecycleEvents.OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(baselineContextIds, await verify.NotificationSuppressionContexts.OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultStatusTransitions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St04PreCommitFailureRollsBackResolutionThenRetryAndReplayRemainIdempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m);
        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var recoveryResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m);
        await using (var baseline = new EePulseDbContext(fixture.Options))
        {
            var projection = await baseline.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            var incident = await baseline.AvailabilityIncidents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(new ThrowBeforeSaveInterceptor()).Options;
            await using (var failing = new EePulseDbContext(failingOptions))
                await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

            await using var afterFailure = new EePulseDbContext(fixture.Options);
            var projectionAfterFailure = await afterFailure.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
            var incidentAfterFailure = await afterFailure.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal((projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId, projection.StateVersion, projection.OpenIncidentId),
                (projectionAfterFailure.UnderlyingStatus, projectionAfterFailure.ConsecutiveFailureCount, projectionAfterFailure.ConsecutiveSuccessCount, projectionAfterFailure.LastFreshEventAt, projectionAfterFailure.WatermarkEventAt, projectionAfterFailure.WatermarkAgentId, projectionAfterFailure.WatermarkResultId, projectionAfterFailure.StateVersion, projectionAfterFailure.OpenIncidentId));
            Assert.Equal((incident.Status, incident.ResolvedAt, incident.ResolvedBy, incident.ResolutionNote), (incidentAfterFailure.Status, incidentAfterFailure.ResolvedAt, incidentAfterFailure.ResolvedBy, incidentAfterFailure.ResolutionNote));
            Assert.Null(await afterFailure.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
            Assert.Null(await afterFailure.ProbeResultStatusTransitions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
            Assert.Single(await afterFailure.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Single(await afterFailure.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using (var retry = new EePulseDbContext(fixture.Options))
            Assert.Equal(recoveryResultId, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        await using (var replay = new EePulseDbContext(fixture.Options))
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await new ProbeResultStatusProcessor(replay, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(AvailabilityIncidentStatus.Resolved, (await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Single(await verify.IncidentLifecycleEvents.Where(row => row.LifecycleEventType == IncidentLifecycleEventType.Resolved).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.NotificationSuppressionContexts.Where(row => row.LifecycleEventKey == IncidentLifecycleEvent.ResolvedLifecycleEventKey).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St03OpeningAtomicallyPersistsOneCompleteAvailabilityIncidentSet()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        var openingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        var alreadyDownResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 0, 1m);

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now.AddMinutes(1)));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using (var belowThreshold = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await belowThreshold.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await belowThreshold.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await belowThreshold.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Null((await belowThreshold.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)).OpenIncidentId);
        }

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now.AddMinutes(1)));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        }

        var openingLedger = await ReadLedgerAsync(fixture, openingResultId);
        await using var verify = new EePulseDbContext(fixture.Options);
        var transition = await verify.ProbeResultStatusTransitions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var lifecycleEvent = await verify.IncidentLifecycleEvents.SingleAsync(TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.SingleAsync(TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Up, transition.FromStatus);
        Assert.Equal(ProbeStatus.Down, transition.ToStatus);
        Assert.Equal("failure-threshold-met", transition.ReasonCode);
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, disposition.Disposition);
        Assert.DoesNotContain(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken), row => row.ResultId == alreadyDownResultId);
        Assert.Equal(incident.Id, projection.OpenIncidentId);
        Assert.Equal(incident.Id, lifecycleEvent.IncidentId);
        Assert.Equal(incident.ProbeId, lifecycleEvent.ProbeId);
        Assert.Equal((fixture.AgentId, openingResultId), (lifecycleEvent.SourceAgentId, lifecycleEvent.SourceResultId));
        Assert.Equal((disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion), (lifecycleEvent.PolicySnapshotId, lifecycleEvent.PolicyVersion));
        Assert.Equal(openingLedger.EndedAt, incident.OpenedAt);
        Assert.Equal(openingLedger.EndedAt, lifecycleEvent.OccurredAt);
        Assert.Equal(openingLedger.ReceivedAt, context.EvaluatedAt);
        Assert.Equal(lifecycleEvent.EventId, context.EventId);
        Assert.Equal(NotificationSuppressionEligibility.Eligible, context.Eligibility);
        Assert.Equal("availability-down", context.ReasonCode);

        verify.Add(new AvailabilityIncident(Guid.NewGuid(), fixture.ProbeId, fixture.Now.AddMinutes(1)));
        await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St03OpeningRetainsAnExistingActiveIncidentAndRepairsItsMissingProjectionPointer()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var beforeOpening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(beforeOpening, new FixedClock(fixture.Now));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var existingIncident = new AvailabilityIncident(Guid.NewGuid(), fixture.ProbeId, fixture.Now.AddSeconds(1));
        await using (var seedActive = new EePulseDbContext(fixture.Options))
        {
            seedActive.Add(existingIncident);
            await seedActive.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await using (var opening = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(opening, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(existingIncident.Id, (await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)).OpenIncidentId);
        Assert.Single(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Contains(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken), transition =>
            transition.ToStatus == ProbeStatus.Down && transition.ReasonCode == "failure-threshold-met");
    }

    [Fact]
    public async Task St03OpeningRetainsAMatchingActiveIncidentProjectionPointer()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var beforeOpening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(beforeOpening, new FixedClock(fixture.Now));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var existingIncident = new AvailabilityIncident(Guid.NewGuid(), fixture.ProbeId, fixture.Now.AddSeconds(1));
        await using (var seedActive = new EePulseDbContext(fixture.Options))
        {
            seedActive.Add(existingIncident);
            var projection = await seedActive.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            seedActive.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = existingIncident.Id;
            await seedActive.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var openingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await using (var opening = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(opening, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(existingIncident.Id, (await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)).OpenIncidentId);
        Assert.Single(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken));
        Assert.Contains(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken), transition =>
            transition.AgentId == fixture.AgentId && transition.ResultId == openingResultId && transition.ToStatus == ProbeStatus.Down);
    }

    [Fact]
    public async Task St03OpeningFailsAndRollsBackWhenProjectionPointsToAnotherDatabaseValidIncident()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var belowThresholdResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var beforeOpening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(beforeOpening, new FixedClock(fixture.Now));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var activeIncident = new AvailabilityIncident(Guid.NewGuid(), fixture.ProbeId, fixture.Now.AddSeconds(1));
        var resolvedIncidentId = Guid.NewGuid();
        await using (var seedInconsistent = new EePulseDbContext(fixture.Options))
        {
            seedInconsistent.Add(activeIncident);
            await seedInconsistent.SaveChangesAsync(TestContext.Current.CancellationToken);
            await seedInconsistent.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({resolvedIncidentId}, {fixture.ProbeId}, {"availability-down"}, {"Resolved"}, {fixture.Now}, {fixture.Now}, {"system-policy"}, {"confirmed-recovery"})", TestContext.Current.CancellationToken);
            var projection = await seedInconsistent.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            seedInconsistent.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = resolvedIncidentId;
            await seedInconsistent.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var openingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        ProbeStatusProjection baselineProjection;
        Guid[] baselineIncidentIds;
        Guid[] baselineLifecycleEventIds;
        Guid[] baselineSuppressionContextEventIds;
        await using (var beforeFailure = new EePulseDbContext(fixture.Options))
        {
            baselineProjection = await beforeFailure.ProbeStatusProjections.AsNoTracking()
                .SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            baselineIncidentIds = await beforeFailure.AvailabilityIncidents.AsNoTracking().OrderBy(incident => incident.Id)
                .Select(incident => incident.Id).ToArrayAsync(TestContext.Current.CancellationToken);
            baselineLifecycleEventIds = await beforeFailure.IncidentLifecycleEvents.AsNoTracking().OrderBy(lifecycleEvent => lifecycleEvent.EventId)
                .Select(lifecycleEvent => lifecycleEvent.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
            baselineSuppressionContextEventIds = await beforeFailure.NotificationSuppressionContexts.AsNoTracking().OrderBy(context => context.EventId)
                .Select(context => context.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        }
        await using (var opening = new EePulseDbContext(fixture.Options))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(opening, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.Contains("inconsistent active availability incident", exception.Message, StringComparison.Ordinal);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var projectionAfterFailure = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(baselineProjection.OpenIncidentId, projectionAfterFailure.OpenIncidentId);
        Assert.Equal(baselineProjection.UnderlyingStatus, projectionAfterFailure.UnderlyingStatus);
        Assert.Equal(baselineProjection.ConsecutiveFailureCount, projectionAfterFailure.ConsecutiveFailureCount);
        Assert.Equal(baselineProjection.ConsecutiveSuccessCount, projectionAfterFailure.ConsecutiveSuccessCount);
        Assert.Equal(baselineProjection.LastFreshEventAt, projectionAfterFailure.LastFreshEventAt);
        Assert.Equal(baselineProjection.WatermarkEventAt, projectionAfterFailure.WatermarkEventAt);
        Assert.Equal(baselineProjection.WatermarkAgentId, projectionAfterFailure.WatermarkAgentId);
        Assert.Equal(baselineProjection.WatermarkResultId, projectionAfterFailure.WatermarkResultId);
        Assert.Equal(baselineProjection.StateVersion, projectionAfterFailure.StateVersion);
        Assert.Equal(resolvedIncidentId, projectionAfterFailure.OpenIncidentId);
        Assert.Equal(ProbeStatus.Up, projectionAfterFailure.UnderlyingStatus);
        Assert.Equal(1, projectionAfterFailure.ConsecutiveFailureCount);
        Assert.Equal(belowThresholdResultId, projectionAfterFailure.WatermarkResultId);
        Assert.Equal(2, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultStatusTransitions.SingleOrDefaultAsync(
            row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId,
            TestContext.Current.CancellationToken));
        Assert.NotNull(await verify.ProbeResultLedgerEntries.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken));
        Assert.Equal(baselineIncidentIds, await verify.AvailabilityIncidents.OrderBy(incident => incident.Id).Select(incident => incident.Id).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(baselineLifecycleEventIds, await verify.IncidentLifecycleEvents.OrderBy(lifecycleEvent => lifecycleEvent.EventId).Select(lifecycleEvent => lifecycleEvent.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(baselineSuppressionContextEventIds, await verify.NotificationSuppressionContexts.OrderBy(context => context.EventId).Select(context => context.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentThresholdOpeningCreatesOneAvailabilityIncidentSet()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var beforeOpening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(beforeOpening, new FixedClock(fixture.Now));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await using var first = new EePulseDbContext(fixture.Options);
        await using var second = new EePulseDbContext(fixture.Options);
        var outcomes = await Task.WhenAll(
            new ProbeResultStatusProcessor(first, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken),
            new ProbeResultStatusProcessor(second, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        Assert.Contains(outcomes, outcome => outcome.Kind == ProbeResultStatusProcessorOutcomeKind.Processed);
        Assert.Contains(outcomes, outcome => outcome.Kind == ProbeResultStatusProcessorOutcomeKind.NoPending);
        await using var verify = new EePulseDbContext(fixture.Options);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(incident.Id, projection.OpenIncidentId);
        Assert.Single(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St03PreCommitFailureRollsBackTheCompleteOpeningSetThenRetryCreatesItOnce()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var bootstrap = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(bootstrap, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var belowThreshold = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(belowThreshold, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        var openingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ThrowBeforeSaveInterceptor()).Options;
        await using (var failing = new EePulseDbContext(failingOptions))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using (var afterFailure = new EePulseDbContext(fixture.Options))
        {
            Assert.Equal(2, await afterFailure.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
            Assert.Single(await afterFailure.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Null((await afterFailure.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).OpenIncidentId);
        }

        await using (var retry = new EePulseDbContext(fixture.Options))
            Assert.Equal(openingResultId, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(3, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.ProbeResultStatusTransitions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessesKernelOutcomesAndAdvancesStateVersionForEveryAppliedResult()
    {
        await using var fixture = await CreateFixtureAsync();
        var bootstrapResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-4), fixture.Now, successes: 3, packetLossRatio: 0m);
        var degradationResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-3), fixture.Now.AddSeconds(1), successes: 3, packetLossRatio: 0m, averageRtt: 500m);
        var sameStateFailureResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-2), fixture.Now.AddSeconds(2), successes: 0, packetLossRatio: 1m);
        var downResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-1), fixture.Now.AddSeconds(3), successes: 0, packetLossRatio: 1m);
        var recoveryPendingResultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now.AddSeconds(4), successes: 3, packetLossRatio: 0m);
        var recoveredResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(5), successes: 3, packetLossRatio: 0m);

        await using var db = new EePulseDbContext(fixture.Options);
        var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now.AddMinutes(1)));
        for (var index = 0; index < 6; index++) Assert.Equal(ProbeResultStatusProcessorOutcomeKind.Processed, (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);

        await using var verify = new EePulseDbContext(fixture.Options);
        var projection = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(ProbeStatus.Up, projection.UnderlyingStatus);
        Assert.Equal(0, projection.ConsecutiveFailureCount);
        Assert.Equal(2, projection.ConsecutiveSuccessCount);
        Assert.Equal(6, projection.StateVersion);
        Assert.Equal(6, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        var transitions = await verify.ProbeResultStatusTransitions.OrderBy(row => row.EventAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, transitions.Count);
        Assert.DoesNotContain(transitions, row => row.ResultId == sameStateFailureResultId);
        Assert.Collection(transitions,
            row => Assert.Equal((bootstrapResultId, "bootstrap-success"), (row.ResultId, row.ReasonCode)),
            row => Assert.Equal((degradationResultId, "quality-degraded"), (row.ResultId, row.ReasonCode)),
            row => Assert.Equal((downResultId, "failure-threshold-met"), (row.ResultId, row.ReasonCode)),
            row => Assert.Equal((recoveryPendingResultId, "recovery-pending"), (row.ResultId, row.ReasonCode)),
            row => Assert.Equal((recoveredResultId, "recovery-threshold-met"), (row.ResultId, row.ReasonCode)));
        var bootstrapLedger = await ReadLedgerAsync(fixture, bootstrapResultId);
        var bootstrapTransition = transitions.Single(row => row.ResultId == bootstrapResultId);
        Assert.Equal(bootstrapLedger.AgentId, bootstrapTransition.AgentId);
        Assert.Equal(bootstrapLedger.ResultId, bootstrapTransition.ResultId);
        Assert.Equal(bootstrapLedger.ProbeId, bootstrapTransition.ProbeId);
        Assert.Equal(bootstrapLedger.EndedAt, bootstrapTransition.EventAt);
        Assert.Equal(bootstrapLedger.ReceivedAt, bootstrapTransition.ReceivedAt);
        var bootstrapDisposition = await verify.ProbeResultProcessingDispositions.SingleAsync(row => row.AgentId == bootstrapTransition.AgentId && row.ResultId == bootstrapTransition.ResultId, TestContext.Current.CancellationToken);
        Assert.Equal(bootstrapDisposition.ResolvedPolicySnapshotId, fixture.PolicyId);
        Assert.Equal(1, bootstrapDisposition.ResolvedPolicyVersion);
    }

    [Fact]
    public async Task St05RecoveryFailedTransitionRetainsTheActiveIncidentAndCreatesOneSuppressedOccurrence()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m, averageRtt: 500m);
        var restoredResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m);
        var recoveryFailedResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(5), fixture.Now.AddSeconds(5), 0, 1m);
        var alreadyDownResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(6), fixture.Now.AddSeconds(6), 0, 1m);

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now));
            for (var index = 0; index < 7; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending,
                (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var transitions = await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(transitions, row => row.ResultId == restoredResultId && row.ReasonCode == "quality-restored" && row.FromStatus == ProbeStatus.Degraded && row.ToStatus == ProbeStatus.Up);
        Assert.Contains(transitions, row => row.ResultId == recoveryFailedResultId && row.ReasonCode == "recovery-failed" && row.FromStatus == ProbeStatus.Recovering && row.ToStatus == ProbeStatus.Down);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
        var occurrence = await verify.IncidentLifecycleEvents.SingleAsync(row => row.SourceResultId == recoveryFailedResultId, TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.SingleAsync(row => row.EventId == occurrence.EventId, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityIncidentStatus.Open, incident.Status);
        Assert.Equal(2, incident.OccurrenceCount);
        Assert.Equal(incident.Id, projection.OpenIncidentId);
        Assert.Equal((IncidentLifecycleEventType.Occurrence, $"occurrence:{recoveryFailedResultId:D}".ToLowerInvariant()),
            (occurrence.LifecycleEventType, occurrence.LifecycleEventKey));
        Assert.Equal((NotificationSuppressionEligibility.Suppressed, "recovery-failed"), (context.Eligibility, context.ReasonCode));
        Assert.False(await verify.IncidentLifecycleEvents.AnyAsync(row => row.SourceResultId == alreadyDownResultId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St05RecoveryFailedRetainsAnAcknowledgedIncidentAndPersistsExactOccurrenceLineage()
    {
        await using var fixture = await CreateFixtureAsync();
        await ProcessToRecoveringAsync(fixture);
        await using (var acknowledge = new EePulseDbContext(fixture.Options))
            await acknowledge.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET status = {"Acknowledged"}, acknowledged_at = {fixture.Now.AddSeconds(3)}, acknowledged_by = {"operator"}, acknowledgement_comment = {"investigating"} WHERE probe_id = {fixture.ProbeId} AND status = {"Open"}", TestContext.Current.CancellationToken);

        var resultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 0, 1m);
        var ledger = await ReadLedgerAsync(fixture, resultId);
        await using (var processing = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var incident = await verify.AvailabilityIncidents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(row => row.ResultId == resultId, TestContext.Current.CancellationToken);
        var occurrence = await verify.IncidentLifecycleEvents.AsNoTracking().SingleAsync(row => row.SourceResultId == resultId, TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.AsNoTracking().SingleAsync(row => row.EventId == occurrence.EventId, TestContext.Current.CancellationToken);

        Assert.Equal(AvailabilityIncidentStatus.Acknowledged, incident.Status);
        Assert.Equal(2, incident.OccurrenceCount);
        Assert.Equal(incident.Id, projection.OpenIncidentId);
        Assert.Equal((fixture.ProbeId, fixture.AgentId, resultId, ProbeResultProcessingDispositionKind.StateDriving),
            (occurrence.ProbeId, occurrence.SourceAgentId, occurrence.SourceResultId, occurrence.ProcessingDisposition));
        Assert.Equal((ProbeStatus.Recovering, ProbeStatus.Down, "recovery-failed"),
            (occurrence.SourceFromStatus, occurrence.SourceToStatus, occurrence.SourceReasonCode));
        Assert.Equal((fixture.PolicyId, 1, $"occurrence:{resultId:D}".ToLowerInvariant(), ledger.EndedAt),
            (occurrence.PolicySnapshotId, occurrence.PolicyVersion, occurrence.LifecycleEventKey, occurrence.OccurredAt));
        Assert.Equal(ledger.ReceivedAt, context.EvaluatedAt);
        Assert.Equal((NotificationSuppressionEligibility.Suppressed, "recovery-failed"), (context.Eligibility, context.ReasonCode));
        Assert.Equal((fixture.AgentId, resultId, ProbeResultProcessingDispositionKind.StateDriving),
            (disposition.AgentId, disposition.ResultId, disposition.Disposition));
    }

    [Theory]
    [InlineData("null-pointer")]
    [InlineData("inactive-pointer")]
    [InlineData("mismatched-pointer")]
    public async Task St05PointerInvariantFailureRollsBackTheCompleteOccurrenceTransaction(string scenario)
    {
        await using var fixture = await CreateFixtureAsync();
        await ProcessToRecoveringAsync(fixture);
        await using (var corrupt = new EePulseDbContext(fixture.Options))
        {
            var projection = await corrupt.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
            if (scenario == "null-pointer")
            {
                corrupt.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = null;
            }
            else
            {
                var inactiveId = Guid.NewGuid();
                await corrupt.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({inactiveId}, {fixture.ProbeId}, {"availability-down"}, {"Resolved"}, {fixture.Now}, {fixture.Now}, {"system-policy"}, {"confirmed-recovery"})", TestContext.Current.CancellationToken);
                if (scenario == "inactive-pointer")
                {
                    var activeOpenedAt = await corrupt.AvailabilityIncidents
                        .Where(incident => incident.ProbeId == fixture.ProbeId && incident.Status == AvailabilityIncidentStatus.Open)
                        .Select(incident => incident.OpenedAt)
                        .SingleAsync(TestContext.Current.CancellationToken);
                    await corrupt.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET status = {"Resolved"}, resolved_at = {activeOpenedAt}, resolved_by = {"system-policy"}, resolution_note = {"confirmed-recovery"} WHERE probe_id = {fixture.ProbeId} AND status = {"Open"}", TestContext.Current.CancellationToken);
                }
                corrupt.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = inactiveId;
            }
            await corrupt.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var resultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 0, 1m);
        var baseline = await CaptureOccurrenceRollbackSnapshotAsync(fixture);
        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.Contains("recovery-failed transition requires", exception.Message, StringComparison.Ordinal);
        }

        var after = await CaptureOccurrenceRollbackSnapshotAsync(fixture);
        AssertOccurrenceRollbackSnapshotEqual(baseline, after);
        Assert.Null(await FindDispositionAsync(fixture, resultId));
        Assert.Null(await FindTransitionAsync(fixture, resultId));
    }

    [Fact]
    public async Task St05PreCommitFailureRollsBackOccurrenceThenRetryAndReplayCreateItExactlyOnce()
    {
        await using var fixture = await CreateFixtureAsync();
        await ProcessToRecoveringAsync(fixture);
        var resultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 0, 1m);
        var baseline = await CaptureOccurrenceRollbackSnapshotAsync(fixture);
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ThrowBeforeSaveInterceptor()).Options;
        await using (var failing = new EePulseDbContext(failingOptions))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        AssertOccurrenceRollbackSnapshotEqual(baseline, await CaptureOccurrenceRollbackSnapshotAsync(fixture));
        Assert.Null(await FindDispositionAsync(fixture, resultId));
        Assert.Null(await FindTransitionAsync(fixture, resultId));

        await using (var retry = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultId, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        await using (var replay = new EePulseDbContext(fixture.Options))
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await new ProbeResultStatusProcessor(replay, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(2, (await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken)).OccurrenceCount);
        Assert.Single(await verify.IncidentLifecycleEvents.Where(row => row.SourceResultId == resultId).ToListAsync(TestContext.Current.CancellationToken));
        var persistedOccurrence = await verify.IncidentLifecycleEvents.AsNoTracking()
            .SingleAsync(row => row.SourceResultId == resultId, TestContext.Current.CancellationToken);
        var persistedContext = await verify.NotificationSuppressionContexts.AsNoTracking()
            .SingleAsync(row => row.EventId == persistedOccurrence.EventId, TestContext.Current.CancellationToken);
        var expectedOccurrenceKey = $"occurrence:{resultId:D}".ToLowerInvariant();
        Assert.Equal(expectedOccurrenceKey, persistedOccurrence.LifecycleEventKey);
        Assert.Equal(expectedOccurrenceKey, persistedContext.LifecycleEventKey);
    }

    [Fact]
    public async Task UsesDeterministicCursorOrderIncludingEqualEndedAtAndCommittedReplayIsIdempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        var eventAt = fixture.Now;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[2]);
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[0]);
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[1]);

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now));
            var processedIds = new List<Guid>();
            for (var index = 0; index < 3; index++)
                processedIds.Add((await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId!.Value);
            Assert.Equal(ids, processedIds);
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(3, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentSameProbeCallsDoNotDuplicateApplication()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);

        await using var first = new EePulseDbContext(fixture.Options);
        await using var second = new EePulseDbContext(fixture.Options);
        await Task.WhenAll(
            new ProbeResultStatusProcessor(first, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken),
            new ProbeResultStatusProcessor(second, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(2, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeResultStatusTransitions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).StateVersion);
        Assert.Equal(2, await verify.ProbeFreshnessExpiryCauses.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreCommitFailureRollsBackProjectionAndDispositionThenRetrySucceeds()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var interceptor = new ThrowBeforeSaveInterceptor();
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(interceptor).Options;

        await using (var failing = new EePulseDbContext(failingOptions))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        }

        await using (var afterFailure = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await afterFailure.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var retry = new EePulseDbContext(fixture.Options);
        Assert.Equal(ProbeResultStatusProcessorOutcomeKind.Processed, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesTheLedgerRowPendingForRetry()
    {
        await using var fixture = await CreateFixtureAsync();
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var blocker = new BlockingSaveInterceptor();
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(blocker).Options;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await using (var interrupted = new EePulseDbContext(options))
        {
            var processing = new ProbeResultStatusProcessor(interrupted, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, cancellation.Token);
            await blocker.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        }

        await using (var afterCancellation = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await afterCancellation.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterCancellation.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterCancellation.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var retry = new EePulseDbContext(fixture.Options);
        Assert.Equal(resultId, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        Assert.Equal(1, await retry.ProbeResultStatusTransitions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestionTransactionWaitsForProcessorLockAndPostReleaseRowIsProcessedNext()
    {
        await using var fixture = await CreateFixtureAsync();
        var firstResultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var blocker = new BlockingSaveInterceptor();
        var processorOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(blocker).Options;
        await using var processorDb = new EePulseDbContext(processorOptions);
        var processing = new ProbeResultStatusProcessor(processorDb, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await blocker.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await using var ingestionDb = new EePulseDbContext(fixture.Options);
        await using var ingestionTransaction = await ingestionDb.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var ingestionBackendProcessId = await ingestionDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(TestContext.Current.CancellationToken);
        var ingestionLock = ProbeTransactionLock.AcquireAsync(ingestionDb, fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var observer = new NpgsqlConnection(fixture.ConnectionString))
        {
            await observer.OpenAsync(TestContext.Current.CancellationToken);
            await WaitForUngrantedProbeLockAsync(observer, ingestionBackendProcessId, fixture.ProbeId, ingestionLock, TestContext.Current.CancellationToken);
        }

        blocker.Release.TrySetResult();
        Assert.Equal(firstResultId, (await processing).ResultId);
        await ingestionLock;
        var secondResultId = Guid.NewGuid();
        ingestionDb.Add(CreateLedger(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m, 1, secondResultId, 1m));
        await ingestionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        await ingestionTransaction.CommitAsync(TestContext.Current.CancellationToken);

        await using (var verify = new EePulseDbContext(fixture.Options))
            Assert.Equal(1, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        await using var nextDb = new EePulseDbContext(fixture.Options);
        Assert.Equal(secondResultId, (await new ProbeResultStatusProcessor(nextDb, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
    }

    [Fact]
    public async Task ResolvesExactSnapshotAndEffectiveBoundaryUsingPersistedPostgresTimestamps()
    {
        await using var fixture = await CreateFixtureAsync();
        var second = await AddConfigurationVersionAsync(fixture, 2, failureThreshold: 1, recoveryThreshold: 1);
        var boundary = await ReadBoundaryAsync(fixture, fixture.AgentId, 2);
        var effectiveBoundaryResultId = await AddLedgerAsync(fixture, boundary, boundary, 3, 0m, configurationVersion: 2);
        var failureResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(1)), boundary.Add(TimeSpan.FromMicroseconds(1)), 0, 1m, configurationVersion: 2);
        var recoveryResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(2)), boundary.Add(TimeSpan.FromMicroseconds(2)), 3, 0m, configurationVersion: 2);
        var earlierBoundaryResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(-1)), boundary.Add(TimeSpan.FromMicroseconds(-1)), 3, 0m, configurationVersion: 2);
        var persistedBoundary = await ReadBoundaryAsync(fixture, fixture.AgentId, 2);
        var effectiveBoundaryLedger = await ReadLedgerAsync(fixture, effectiveBoundaryResultId);
        var failureLedger = await ReadLedgerAsync(fixture, failureResultId);
        var recoveryLedger = await ReadLedgerAsync(fixture, recoveryResultId);
        var earlierBoundaryLedger = await ReadLedgerAsync(fixture, earlierBoundaryResultId);

        Assert.Equal(persistedBoundary, effectiveBoundaryLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(-1)), earlierBoundaryLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(1)), failureLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(2)), recoveryLedger.ReceivedAt);

        await using var db = new EePulseDbContext(fixture.Options);
        var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now));
        for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var dispositions = await verify.ProbeResultProcessingDispositions.OrderBy(row => row.EventAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(dispositions, row => row.ResultId == earlierBoundaryResultId && row.Disposition == ProbeResultProcessingDispositionKind.HistoricalOther && row.ReasonCode == "config-not-effective");
        Assert.Empty(await verify.ProbeFreshnessExpiryCauses.AsNoTracking().Where(cause => cause.SourceResultId == earlierBoundaryResultId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Contains(dispositions, row => row.ResultId == effectiveBoundaryResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.All(dispositions.Where(row => row.ResolvedPolicySnapshotId.HasValue), row => Assert.Equal(second.PolicyId, row.ResolvedPolicySnapshotId));
        Assert.Equal(ProbeStatus.Up, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).UnderlyingStatus);
        Assert.Contains(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken), row =>
            row.ResultId == recoveryResultId && row.ReasonCode == "recovery-threshold-met" && row.FromStatus == ProbeStatus.Down && row.ToStatus == ProbeStatus.Up);
    }

    [Fact]
    public async Task HistoricalOnlyLineageAndCursorAndTimeOutcomesDoNotCreateOrMutateProjection()
    {
        await using var fixture = await CreateFixtureAsync(includeBinding: false);
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var db = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var verify = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await verify.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("policy-lineage-unresolved", disposition.ReasonCode);
            Assert.Empty(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.ProbeFreshnessExpiryCauses.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var timed = await CreateFixtureAsync();
        var boundary = await ReadBoundaryAsync(timed, timed.AgentId, 1);
        var latenessEqualityResultId = await AddLedgerAsync(timed, boundary.AddMinutes(-5), boundary, 3, 0m);
        var beyondLatenessResultId = await AddLedgerAsync(timed, boundary.AddMinutes(-5).Add(TimeSpan.FromMicroseconds(-1)), boundary, 3, 0m);
        var futureSkewEqualityResultId = await AddLedgerAsync(timed, boundary.AddSeconds(60), boundary, 3, 0m);
        var futureSkewResultId = await AddLedgerAsync(timed, boundary.AddSeconds(60).Add(TimeSpan.FromMicroseconds(1)), boundary, 3, 0m);
        var persistedBoundary = await ReadBoundaryAsync(timed, timed.AgentId, 1);
        var latenessEqualityLedger = await ReadLedgerAsync(timed, latenessEqualityResultId);
        var beyondLatenessLedger = await ReadLedgerAsync(timed, beyondLatenessResultId);
        var futureSkewEqualityLedger = await ReadLedgerAsync(timed, futureSkewEqualityResultId);
        var futureSkewLedger = await ReadLedgerAsync(timed, futureSkewResultId);

        Assert.Equal(persistedBoundary, latenessEqualityLedger.ReceivedAt);
        Assert.Equal(latenessEqualityLedger.ReceivedAt.AddMinutes(-5), latenessEqualityLedger.EndedAt);
        Assert.Equal(beyondLatenessLedger.ReceivedAt.AddMinutes(-5).Add(TimeSpan.FromMicroseconds(-1)), beyondLatenessLedger.EndedAt);
        Assert.Equal(futureSkewEqualityLedger.ReceivedAt.AddSeconds(60), futureSkewEqualityLedger.EndedAt);
        Assert.Equal(futureSkewLedger.ReceivedAt.AddSeconds(60).Add(TimeSpan.FromMicroseconds(1)), futureSkewLedger.EndedAt);
        await using (var db = new EePulseDbContext(timed.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(timed.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(timed.ProbeId, TestContext.Current.CancellationToken);
        }
        await using var timeVerify = new EePulseDbContext(timed.Options);
        Assert.Equal(2, (await timeVerify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).StateVersion);
        var timedDispositions = await timeVerify.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(timedDispositions, row => row.ResultId == latenessEqualityResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.Contains(timedDispositions, row => row.ResultId == beyondLatenessResultId && row.ReasonCode == "beyond-approved-lateness");
        Assert.Contains(timedDispositions, row => row.ResultId == futureSkewEqualityResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.Contains(timedDispositions, row => row.ResultId == futureSkewResultId && row.ReasonCode == "future-or-skew-suspect");
        var timedTransitions = await timeVerify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(timedTransitions);
        Assert.DoesNotContain(timedTransitions, row => row.ResultId == beyondLatenessResultId || row.ResultId == futureSkewResultId);
        Assert.Empty(await timeVerify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await timeVerify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await timeVerify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await timeVerify.ProbeFreshnessExpiryCauses.CountAsync(TestContext.Current.CancellationToken));

        await using var cursor = await CreateFixtureAsync();
        await AddLedgerAsync(cursor, cursor.Now, cursor.Now, 3, 0m);
        await using (var db = new EePulseDbContext(cursor.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(cursor.Now)).ProcessNextAsync(cursor.ProbeId, TestContext.Current.CancellationToken);
        await AddLedgerAsync(cursor, cursor.Now.AddSeconds(-1), cursor.Now, 3, 0m);
        await using (var db = new EePulseDbContext(cursor.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(cursor.Now)).ProcessNextAsync(cursor.ProbeId, TestContext.Current.CancellationToken);
        await using (var cursorVerify = new EePulseDbContext(cursor.Options))
        {
            Assert.Contains(await cursorVerify.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken), row => row.ReasonCode == "late-order");
            Assert.Single(await cursorVerify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await cursorVerify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await cursorVerify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await cursorVerify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Single(await cursorVerify.ProbeFreshnessExpiryCauses.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var noBoundary = await CreateFixtureAsync(includeBoundary: false);
        await AddLedgerAsync(noBoundary, noBoundary.Now, noBoundary.Now, 3, 0m);
        await using (var db = new EePulseDbContext(noBoundary.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(noBoundary.Now)).ProcessNextAsync(noBoundary.ProbeId, TestContext.Current.CancellationToken);
        await using (var boundaryVerify = new EePulseDbContext(noBoundary.Options))
        {
            Assert.Empty(await boundaryVerify.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal("policy-lineage-unresolved", (await boundaryVerify.ProbeResultProcessingDispositions.SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
            Assert.Empty(await boundaryVerify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await boundaryVerify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await boundaryVerify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await boundaryVerify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await boundaryVerify.ProbeFreshnessExpiryCauses.ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("device")]
    [InlineData("agent-group")]
    public async Task St08DisabledSchedulingOwnershipPersistsOnlyTheDisabledDisposition(string disabledOwner)
    {
        await using var fixture = await CreateFixtureAsync();
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var ledger = await ReadLedgerAsync(fixture, resultId);
        await SetSchedulingOwnerEnabledAsync(fixture, disabledOwner, false);

        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var outcome = await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now))
                .ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal((ProbeResultStatusProcessorOutcomeKind.Processed, fixture.AgentId, resultId, ProbeResultProcessingDispositionKind.Disabled),
                (outcome.Kind, outcome.AgentId, outcome.ResultId, outcome.Disposition));
        }

        await using (var verify = new EePulseDbContext(fixture.Options))
        {
            var disposition = await verify.ProbeResultProcessingDispositions.AsNoTracking()
                .SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
            Assert.Equal((ledger.AgentId, ledger.ResultId, ledger.ProbeId, ProbeResultProcessingDispositionKind.Disabled, "disabled",
                    fixture.PolicyId, 1, fixture.Now),
                (disposition.AgentId, disposition.ResultId, disposition.ProbeId, disposition.Disposition, disposition.ReasonCode,
                    disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion, disposition.DecidedAt));
            Assert.Empty(await verify.ProbeStatusProjections.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.ProbeResultStatusTransitions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.AvailabilityIncidents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.IncidentLifecycleEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.NotificationSuppressionContexts.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var replay = new EePulseDbContext(fixture.Options);
        Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending,
            (await new ProbeResultStatusProcessor(replay, new FixedClock(fixture.Now))
                .ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
    }

    [Theory]
    [InlineData("probe", false)]
    [InlineData("device", true)]
    [InlineData("agent-group", false)]
    public async Task St08DisabledSchedulingOwnershipDoesNotMutateAnActiveIncidentOrProjection(string disabledOwner, bool acknowledgeIncident)
    {
        await using var fixture = await CreateFixtureAsync();
        await ProcessToRecoveringAsync(fixture);
        if (acknowledgeIncident)
        {
            await using var acknowledge = new EePulseDbContext(fixture.Options);
            await acknowledge.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET status = {"Acknowledged"}, acknowledged_at = {fixture.Now.AddSeconds(3)}, acknowledged_by = {"operator"}, acknowledgement_comment = {"investigating"} WHERE probe_id = {fixture.ProbeId} AND status = {"Open"}", TestContext.Current.CancellationToken);
        }

        var baseline = await CaptureDisabledNonMutationSnapshotAsync(fixture);
        await SetSchedulingOwnerEnabledAsync(fixture, disabledOwner, false);
        var resultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 0, 1m);
        await using (var processing = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        Assert.Equal(baseline, await CaptureDisabledNonMutationSnapshotAsync(fixture));
        await using var verify = new EePulseDbContext(fixture.Options);
        var disposition = await verify.ProbeResultProcessingDispositions.AsNoTracking()
            .SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
        Assert.Equal((ProbeResultProcessingDispositionKind.Disabled, "disabled", fixture.PolicyId, 1, fixture.Now),
            (disposition.Disposition, disposition.ReasonCode, disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion, disposition.DecidedAt));
    }

    [Fact]
    public async Task St08DisabledDispositionPrecedencePreservesLineageAndConfigurationOutcomesThenWinsOverCursorAndTime()
    {
        await using (var lineage = await CreateFixtureAsync(includeBinding: false))
        {
            await SetSchedulingOwnerEnabledAsync(lineage, "probe", false);
            var resultId = await AddLedgerAsync(lineage, lineage.Now, lineage.Now, 3, 0m);
            await using var processing = new EePulseDbContext(lineage.Options);
            Assert.Equal(ProbeResultProcessingDispositionKind.HistoricalOther,
                (await new ProbeResultStatusProcessor(processing, new FixedClock(lineage.Now)).ProcessNextAsync(lineage.ProbeId, TestContext.Current.CancellationToken)).Disposition);
            Assert.Equal((ProbeResultProcessingDispositionKind.HistoricalOther, "policy-lineage-unresolved"),
                ((await FindDispositionAsync(lineage, resultId))!.Disposition, (await FindDispositionAsync(lineage, resultId))!.ReasonCode));
        }

        await using (var configuration = await CreateFixtureAsync())
        {
            await SetSchedulingOwnerEnabledAsync(configuration, "probe", false);
            var resultId = await AddLedgerAsync(configuration, configuration.Now.AddSeconds(-1), configuration.Now.AddSeconds(-1), 3, 0m);
            await using var processing = new EePulseDbContext(configuration.Options);
            await new ProbeResultStatusProcessor(processing, new FixedClock(configuration.Now)).ProcessNextAsync(configuration.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal((ProbeResultProcessingDispositionKind.HistoricalOther, "config-not-effective"),
                ((await FindDispositionAsync(configuration, resultId))!.Disposition, (await FindDispositionAsync(configuration, resultId))!.ReasonCode));
        }

        await using (var cursor = await CreateFixtureAsync())
        {
            await AddLedgerAsync(cursor, cursor.Now, cursor.Now, 3, 0m);
            await using (var bootstrap = new EePulseDbContext(cursor.Options))
                await new ProbeResultStatusProcessor(bootstrap, new FixedClock(cursor.Now)).ProcessNextAsync(cursor.ProbeId, TestContext.Current.CancellationToken);
            await SetSchedulingOwnerEnabledAsync(cursor, "probe", false);
            var resultId = await AddLedgerAsync(cursor, cursor.Now.AddSeconds(-1), cursor.Now, 3, 0m);
            await using var processing = new EePulseDbContext(cursor.Options);
            await new ProbeResultStatusProcessor(processing, new FixedClock(cursor.Now)).ProcessNextAsync(cursor.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal((ProbeResultProcessingDispositionKind.Disabled, "disabled"),
                ((await FindDispositionAsync(cursor, resultId))!.Disposition, (await FindDispositionAsync(cursor, resultId))!.ReasonCode));
        }

        await AssertDisabledWinsOverTimeRuleAsync(TimeSpan.FromMinutes(-5).Add(TimeSpan.FromMicroseconds(-1)), "beyond-approved-lateness");
        await AssertDisabledWinsOverTimeRuleAsync(TimeSpan.FromSeconds(60).Add(TimeSpan.FromMicroseconds(1)), "future-or-skew-suspect");
    }

    [Fact]
    public async Task St08PreCommitFailureRollsBackDisabledDispositionThenRetryAndReplayPersistItOnce()
    {
        await using var fixture = await CreateFixtureAsync();
        await SetSchedulingOwnerEnabledAsync(fixture, "probe", false);
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var baseline = await CaptureDisabledNonMutationSnapshotAsync(fixture);
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ThrowBeforeSaveInterceptor()).Options;
        await using (var failing = new EePulseDbContext(failingOptions))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now))
                .ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        Assert.Equal(baseline, await CaptureDisabledNonMutationSnapshotAsync(fixture));
        Assert.Null(await FindDispositionAsync(fixture, resultId));
        await using (var retry = new EePulseDbContext(fixture.Options))
            Assert.Equal(ProbeResultProcessingDispositionKind.Disabled,
                (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Disposition);
        await using (var replay = new EePulseDbContext(fixture.Options))
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending,
                (await new ProbeResultStatusProcessor(replay, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Single(await verify.ProbeResultProcessingDispositions.Where(row => row.AgentId == fixture.AgentId && row.ResultId == resultId).ToListAsync(TestContext.Current.CancellationToken));
    }

    private static async Task ProcessToRecoveringAsync(Fixture fixture)
    {
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m);
        await using var processing = new EePulseDbContext(fixture.Options);
        var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
        for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task St09cStateDrivingResultMaterializesItsImmutableFreshnessCauseAfterTheSourceFlush()
    {
        await using var fixture = await CreateFixtureAsync();
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var applicationClock = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        await using (var processing = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(processing, new FixedClock(applicationClock)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var cause = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((fixture.ProbeId, fixture.AgentId, resultId, fixture.Now, fixture.Now, 1L, fixture.GroupId),
            (cause.ProbeId, cause.SourceAgentId, cause.SourceResultId, cause.SourceCursorEventAt, cause.SourceLastFreshEventAt, cause.SourceConfigurationVersion, cause.SourceAgentGroupId));
        Assert.Equal((disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion), (cause.PolicySnapshotId, cause.PolicyVersion));
        Assert.Equal((30, 60, fixture.Now.AddSeconds(60)), (cause.FreshnessIntervalSeconds, cause.FreshnessGraceSeconds, cause.DueAt));
        Assert.InRange(cause.RequestedAt, before, after);
        Assert.NotEqual(applicationClock, cause.RequestedAt);
        Assert.Equal((fixture.Now, fixture.AgentId, resultId, fixture.Now), (projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId, projection.LastFreshEventAt));
        Assert.Equal(1, projection.StateVersion);

        var laterResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var laterProcessing = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(laterProcessing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var afterLater = new EePulseDbContext(fixture.Options);
        var laterProjection = await afterLater.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, laterProjection.StateVersion);
        var causesBeforeReplay = await afterLater.ProbeFreshnessExpiryCauses.AsNoTracking().OrderBy(row => row.CauseId)
            .Select(row => new { row.CauseId, row.SourceAgentId, row.SourceResultId }).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, causesBeforeReplay.Length);
        Assert.Single(causesBeforeReplay, cause => cause.SourceResultId == resultId);
        Assert.Single(causesBeforeReplay, cause => cause.SourceResultId == laterResultId);
        Assert.Contains(laterResultId, await afterLater.ProbeResultProcessingDispositions.AsNoTracking()
            .Select(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken));

        await using var replay = new EePulseDbContext(fixture.Options);
        Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending,
            (await new ProbeResultStatusProcessor(replay, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        await using var afterReplay = new EePulseDbContext(fixture.Options);
        var causesAfterReplay = await afterReplay.ProbeFreshnessExpiryCauses.AsNoTracking().OrderBy(row => row.CauseId)
            .Select(row => new { row.CauseId, row.SourceAgentId, row.SourceResultId }).ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, causesAfterReplay.Length);
        Assert.True(causesBeforeReplay.SequenceEqual(causesAfterReplay));
        Assert.Single(causesAfterReplay, cause => cause.SourceResultId == resultId);
        Assert.Single(causesAfterReplay, cause => cause.SourceResultId == laterResultId);
    }

    [Fact]
    public async Task St09cStateDrivingFailureWithoutAVisibleTransitionStillMaterializesACause()
    {
        await using var fixture = await CreateFixtureAsync();
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 0, 1m);
        await using (var processing = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Empty(await verify.ProbeResultStatusTransitions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.ProbeFreshnessExpiryCauses.AsNoTracking().Where(cause => cause.SourceResultId == resultId).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St09cUsesTheExactSourceConfigurationInsteadOfCurrentOrLaterConfiguration()
    {
        await using var fixture = await CreateFixtureAsync();
        await using (var current = new EePulseDbContext(fixture.Options))
            await current.Database.ExecuteSqlInterpolatedAsync($"UPDATE probes SET interval_seconds = {5} WHERE id = {fixture.ProbeId}", TestContext.Current.CancellationToken);
        await AddConfigurationVersionAsync(fixture, 2, 2, 2);
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m, configurationVersion: 1);

        await using (var processing = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var cause = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(cause => cause.SourceResultId == resultId, TestContext.Current.CancellationToken);
        Assert.Equal((1L, 30, fixture.Now.AddSeconds(60)), (cause.SourceConfigurationVersion, cause.FreshnessIntervalSeconds, cause.DueAt));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"probes\":{}}")]
    [InlineData("{\"probes\":[]}")]
    [InlineData("{\"probes\":[{\"probeId\":\"{0}\",\"intervalSeconds\":30},{\"probeId\":\"{0}\",\"intervalSeconds\":30}]}")]
    [InlineData("{\"probes\":[{\"probeId\":\"{0}\",\"intervalSeconds\":\"30\"}]}")]
    [InlineData("{\"probes\":[{\"probeId\":\"{0}\",\"intervalSeconds\":30.5}]}")]
    [InlineData("{\"probes\":[{\"probeId\":\"{0}\",\"intervalSeconds\":0}]}")]
    [InlineData("{\"probes\":[{\"probeId\":\"{0}\",\"intervalSeconds\":-1}]}")]
    [InlineData("{\"probes\":[{\"probeId\":\"{0}\",\"intervalSeconds\":999999999999999999999}]}")]
    public async Task St09cMalformedImmutableSourcePayloadRollsBackTheEntireResultTransaction(string payloadTemplate)
    {
        await using var fixture = await CreateFixtureAsync();
        var payload = payloadTemplate.Replace(
            "{0}",
            fixture.ProbeId.ToString("D"),
            StringComparison.Ordinal);
        await using (var corrupt = new EePulseDbContext(fixture.Options))
            await corrupt.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_configuration_snapshots SET payload = {payload}::jsonb WHERE agent_group_id = {fixture.GroupId} AND version = {1L}", TestContext.Current.CancellationToken);
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);

        await using (var processing = new EePulseDbContext(fixture.Options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Empty(await verify.ProbeResultProcessingDispositions.AsNoTracking().Where(row => row.ResultId == resultId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.ProbeStatusProjections.AsNoTracking().Where(row => row.ProbeId == fixture.ProbeId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.ProbeResultStatusTransitions.AsNoTracking().Where(row => row.ResultId == resultId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.AvailabilityIncidents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.IncidentLifecycleEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.NotificationSuppressionContexts.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.ProbeFreshnessExpiryCauses.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St09cSecondCauseFlushFailureRollsBackTheFirstFlushAndRetryCreatesOneCoherentSet()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var baselineProcessing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(baselineProcessing, new FixedClock(fixture.Now));
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }
        var resultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        var baseline = await CaptureSt09cRollbackSnapshotAsync(fixture);
        var interceptor = new ThrowOnSecondSaveInterceptor();
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(interceptor).Options;

        await using (var failing = new EePulseDbContext(failingOptions))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using (var afterFailure = new EePulseDbContext(fixture.Options))
        {
            var afterRollback = await CaptureSt09cRollbackSnapshotAsync(fixture);
            Assert.Equal(baseline.Projection, afterRollback.Projection);
            Assert.True(baseline.Dispositions.SequenceEqual(afterRollback.Dispositions));
            Assert.True(baseline.Transitions.SequenceEqual(afterRollback.Transitions));
            Assert.True(baseline.Incidents.SequenceEqual(afterRollback.Incidents));
            Assert.True(baseline.EventIds.SequenceEqual(afterRollback.EventIds));
            Assert.True(baseline.ContextIds.SequenceEqual(afterRollback.ContextIds));
            Assert.True(baseline.Causes.SequenceEqual(afterRollback.Causes));
            Assert.Empty(await afterFailure.ProbeResultProcessingDispositions.AsNoTracking().Where(row => row.AgentId == fixture.AgentId && row.ResultId == resultId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.ProbeResultStatusTransitions.AsNoTracking().Where(row => row.AgentId == fixture.AgentId && row.ResultId == resultId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(await afterFailure.ProbeResultLedgerEntries.AsNoTracking().SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken));
        }

        await using (var retry = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using var verify = new EePulseDbContext(fixture.Options);
        var disposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
        var transition = await verify.ProbeResultStatusTransitions.AsNoTracking().SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
        var incident = await verify.AvailabilityIncidents.AsNoTracking().SingleAsync(row => row.ProbeId == fixture.ProbeId && row.Status == AvailabilityIncidentStatus.Open, TestContext.Current.CancellationToken);
        var lifecycle = await verify.IncidentLifecycleEvents.AsNoTracking().SingleAsync(row => row.IncidentId == incident.Id, TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.AsNoTracking().SingleAsync(row => row.EventId == lifecycle.EventId, TestContext.Current.CancellationToken);
        var cause = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(row => row.SourceAgentId == fixture.AgentId && row.SourceResultId == resultId && row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, disposition.Disposition);
        Assert.Equal((ProbeStatus.Up, ProbeStatus.Down, "failure-threshold-met"), (transition.FromStatus, transition.ToStatus, transition.ReasonCode));
        Assert.Equal((IncidentLifecycleEventType.Opened, NotificationSuppressionEligibility.Eligible), (lifecycle.LifecycleEventType, context.Eligibility));
        Assert.Equal((ProbeStatus.Down, 2, 0, fixture.Now.AddSeconds(2), fixture.AgentId, resultId, fixture.Now.AddSeconds(2), incident.Id, 3L),
            (projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId, projection.LastFreshEventAt, projection.OpenIncidentId, projection.StateVersion));
        Assert.Equal(resultId, cause.SourceResultId);
        await using (var replay = new EePulseDbContext(fixture.Options))
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await new ProbeResultStatusProcessor(replay, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        await using var afterReplay = new EePulseDbContext(fixture.Options);
        Assert.Single(await afterReplay.ProbeResultProcessingDispositions.AsNoTracking().Where(row => row.AgentId == fixture.AgentId && row.ResultId == resultId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await afterReplay.ProbeFreshnessExpiryCauses.AsNoTracking().Where(row => row.SourceAgentId == fixture.AgentId && row.SourceResultId == resultId && row.ProbeId == fixture.ProbeId).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10DueCauseTransitionsOnlyVisibleStatusToUnknownAndPreservesTheUnderlyingState()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var results = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        var beforeCutoff = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using (var expiry = new EePulseDbContext(fixture.Options))
        {
            var outcome = await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.Applied, outcome.CauseId,
                ProbeFreshnessExpiryCauseDispositionOutcome.Applied, ProbeFreshnessExpiryCauseDisposition.ResultFreshnessExpiredReasonCode), outcome);
        }
        var afterCutoff = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var cause = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        var transition = await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((ProbeStatus.Up, ProbeStatus.Unknown, 0, 1, fixture.Now, fixture.Now, fixture.AgentId),
            (projection.UnderlyingStatus, projection.VisibleStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount,
                projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId));
        Assert.Equal((cause.CauseId, ProbeFreshnessExpiryCauseDispositionOutcome.Applied,
                ProbeFreshnessExpiryCauseDisposition.ResultFreshnessExpiredReasonCode, disposition.ExpiryCutoffReceivedAt, disposition.ExpiryCutoffReceivedAt),
            (disposition.CauseId, disposition.Outcome, disposition.ReasonCode, disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt));
        Assert.Equal((cause.CauseId, ProbeStatus.Up, ProbeStatus.Unknown, disposition.AppliedAt),
            (transition.CauseId, transition.FromVisibleStatus, transition.ToVisibleStatus, (DateTimeOffset?)transition.AppliedAt));
        Assert.InRange(disposition.ExpiryCutoffReceivedAt, beforeCutoff, afterCutoff);
        Assert.Equal((disposition.ExpiryCutoffReceivedAt, disposition.ExpiryCutoffReceivedAt), (disposition.AppliedAt, (DateTimeOffset?)transition.AppliedAt));
        Assert.Empty(await verify.AvailabilityIncidents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.IncidentLifecycleEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.NotificationSuppressionContexts.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10DrainsAllPreCutoffResultsBeforeSelectingTheEarliestDueCause()
    {
        await using var fixture = await CreateFixtureAsync();
        var firstResultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var first = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(first, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var laterResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);

        await using (var expiry = new EePulseDbContext(fixture.Options))
        {
            var outcome = await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.NoOp, outcome.CauseId,
                ProbeFreshnessExpiryCauseDispositionOutcome.NoOp, ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode), outcome);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var firstCause = await verify.ProbeFreshnessExpiryCauses.SingleAsync(row => row.SourceResultId == firstResultId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeFreshnessExpiryCauseDispositions.SingleAsync(TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((firstCause.CauseId, ProbeFreshnessExpiryCauseDispositionOutcome.NoOp,
            ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode), (disposition.CauseId, disposition.Outcome, disposition.ReasonCode));
        Assert.Equal((fixture.Now.AddSeconds(1), fixture.AgentId, laterResultId, ProbeStatus.Up),
            (projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId, projection.VisibleStatus));
        Assert.NotNull(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.ResultId == laterResultId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10ProjectionMissingAndVisibleAlreadyUnknownAreDeterministicNoOps()
    {
        await using (var missing = await CreateFixtureAsync())
        {
            await AddLedgerAsync(missing, missing.Now, missing.Now, 3, 0m);
            await using (var results = new EePulseDbContext(missing.Options))
                await new ProbeResultStatusProcessor(results, new FixedClock(missing.Now)).ProcessNextAsync(missing.ProbeId, TestContext.Current.CancellationToken);
            await using (var delete = new EePulseDbContext(missing.Options))
                await delete.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_status_projections WHERE probe_id = {missing.ProbeId}", TestContext.Current.CancellationToken);
            await using (var expiry = new EePulseDbContext(missing.Options))
            {
                var causeId = await expiry.ProbeFreshnessExpiryCauses.Select(row => row.CauseId).SingleAsync(TestContext.Current.CancellationToken);
                Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.NoOp, causeId,
                    ProbeFreshnessExpiryCauseDispositionOutcome.NoOp, ProbeFreshnessExpiryCauseDisposition.ProjectionMissingReasonCode),
                    await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(missing.ProbeId, TestContext.Current.CancellationToken));
            }
            await using var verify = new EePulseDbContext(missing.Options);
            Assert.Equal(ProbeFreshnessExpiryCauseDisposition.ProjectionMissingReasonCode,
                (await verify.ProbeFreshnessExpiryCauseDispositions.SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
        }

        await using (var unknown = await CreateFixtureAsync())
        {
            await AddLedgerAsync(unknown, unknown.Now, unknown.Now, 3, 0m);
            await using (var results = new EePulseDbContext(unknown.Options))
                await new ProbeResultStatusProcessor(results, new FixedClock(unknown.Now)).ProcessNextAsync(unknown.ProbeId, TestContext.Current.CancellationToken);
            await using (var setUnknown = new EePulseDbContext(unknown.Options))
                await setUnknown.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET visible_status = {"Unknown"} WHERE probe_id = {unknown.ProbeId}", TestContext.Current.CancellationToken);
            await using (var expiry = new EePulseDbContext(unknown.Options))
            {
                var causeId = await expiry.ProbeFreshnessExpiryCauses.Select(row => row.CauseId).SingleAsync(TestContext.Current.CancellationToken);
                Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.NoOp, causeId,
                    ProbeFreshnessExpiryCauseDispositionOutcome.NoOp, ProbeFreshnessExpiryCauseDisposition.VisibleAlreadyUnknownReasonCode),
                    await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(unknown.ProbeId, TestContext.Current.CancellationToken));
            }
            await using var verify = new EePulseDbContext(unknown.Options);
            Assert.Equal(ProbeFreshnessExpiryCauseDisposition.VisibleAlreadyUnknownReasonCode,
                (await verify.ProbeFreshnessExpiryCauseDispositions.SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
        }
    }

    [Fact]
    public async Task St10FailureRollsBackTheExpiryDispositionAndReplayPersistsItOnce()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var results = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ThrowBeforeSaveInterceptor()).Options;

        await using (var failing = new EePulseDbContext(failingOptions))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeFreshnessExpiryCauseProcessor(failing).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        await using (var afterFailure = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await afterFailure.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(ProbeStatus.Up, (await afterFailure.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).VisibleStatus);
        }

        await using (var retry = new EePulseDbContext(fixture.Options))
            await new ProbeFreshnessExpiryCauseProcessor(retry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var replay = new EePulseDbContext(fixture.Options))
            Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.NoDueCause),
                await new ProbeFreshnessExpiryCauseProcessor(replay).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Single(await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Up", 0, 1)]
    [InlineData("Degraded", 0, 1)]
    [InlineData("Down", 1, 0)]
    [InlineData("Recovering", 0, 1)]
    public async Task St10AppliesFromEveryKnownVisibleStatusWithoutChangingUnderlyingState(
        string visibleStatus, int failures, int successes)
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var results = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var arrange = new EePulseDbContext(fixture.Options))
            await arrange.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET underlying_status = {visibleStatus}, visible_status = {visibleStatus}, consecutive_failure_count = {failures}, consecutive_success_count = {successes} WHERE probe_id = {fixture.ProbeId}", TestContext.Current.CancellationToken);
        await using (var before = new EePulseDbContext(fixture.Options))
        {
            var projection = await before.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            var cause = await before.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            await using var expiry = new EePulseDbContext(fixture.Options);
            var outcome = await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.Applied, cause.CauseId,
                ProbeFreshnessExpiryCauseDispositionOutcome.Applied, ProbeFreshnessExpiryCauseDisposition.ResultFreshnessExpiredReasonCode), outcome);
            await using var verify = new EePulseDbContext(fixture.Options);
            var after = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            var disposition = await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            var transition = await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal((projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount,
                projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId,
                projection.OpenIncidentId, projection.StateVersion + 1),
                (after.UnderlyingStatus, after.ConsecutiveFailureCount, after.ConsecutiveSuccessCount,
                    after.LastFreshEventAt, after.WatermarkEventAt, after.WatermarkAgentId, after.WatermarkResultId,
                    after.OpenIncidentId, after.StateVersion));
            Assert.Equal(ProbeStatus.Unknown, after.VisibleStatus);
            Assert.Equal((cause.CauseId, ProbeFreshnessExpiryCauseDispositionOutcome.Applied,
                ProbeFreshnessExpiryCauseDisposition.ResultFreshnessExpiredReasonCode),
                (disposition.CauseId, disposition.Outcome, disposition.ReasonCode));
            Assert.Equal((cause.CauseId, Enum.Parse<ProbeStatus>(visibleStatus), ProbeStatus.Unknown, disposition.AppliedAt),
                (transition.CauseId, transition.FromVisibleStatus, transition.ToVisibleStatus, (DateTimeOffset?)transition.AppliedAt));
        }
    }

    [Fact]
    public async Task St10LastFreshEventOnlyDifferenceIsSourceSupersededWithoutVisibleMutation()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var results = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var alter = new EePulseDbContext(fixture.Options))
            await alter.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET last_fresh_event_at = {fixture.Now.AddSeconds(1)} WHERE probe_id = {fixture.ProbeId}", TestContext.Current.CancellationToken);
        await using (var expiry = new EePulseDbContext(fixture.Options))
        {
            var causeId = await expiry.ProbeFreshnessExpiryCauses.Select(row => row.CauseId).SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.NoOp, causeId,
                ProbeFreshnessExpiryCauseDispositionOutcome.NoOp, ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode),
                await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        }
        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(ProbeStatus.Up, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).VisibleStatus);
        Assert.Empty(await verify.ProbeFreshnessExpiryCauseTransitions.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10SelectsOnlyOneDueCauseInDueRequestedCauseOrderAndLeavesTheOthersUndisposed()
    {
        await using var fixture = await CreateFixtureAsync();
        var sourceResultIds = new[]
        {
            await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m),
            await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m),
            await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 3, 0m),
            await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m),
        };
        await using (var results = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now));
            for (var index = 0; index < sourceResultIds.Length; index++)
                await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        Guid remainingCauseId;
        await using (var normalize = new EePulseDbContext(fixture.Options))
        {
            var causes = await normalize.ProbeFreshnessExpiryCauses.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
            var bySource = causes.ToDictionary(row => row.SourceResultId);
            var earliestDue = bySource[sourceResultIds[0]];
            var earliestRequested = bySource[sourceResultIds[1]];
            var lowerCauseIdTie = bySource[sourceResultIds[2]];
            var higherCauseIdTie = bySource[sourceResultIds[3]];
            var dueAtWinnerCauseId = Guid.Parse("00000000-0000-0000-0000-000000000201");
            var requestedAtWinnerCauseId = Guid.Parse("00000000-0000-0000-0000-000000000202");
            var lowerCauseIdWinner = Guid.Parse("00000000-0000-0000-0000-000000000101");
            remainingCauseId = Guid.Parse("00000000-0000-0000-0000-000000000102");
            var dueAt = fixture.Now.AddMinutes(5);
            var requestedAt = fixture.Now.AddDays(-1);
            // Test-only ordering-fixture setup: RequestedAt is database-generated and causes are append-only.
            await normalize.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_causes DISABLE TRIGGER tr_probe_freshness_expiry_causes_append_only", TestContext.Current.CancellationToken);
            try
            {
                await normalize.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_freshness_expiry_causes SET cause_id = {dueAtWinnerCauseId}, due_at = {dueAt}, requested_at = {requestedAt} WHERE source_result_id = {earliestDue.SourceResultId}", TestContext.Current.CancellationToken);
                await normalize.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_freshness_expiry_causes SET cause_id = {requestedAtWinnerCauseId}, due_at = {dueAt.AddSeconds(1)}, requested_at = {requestedAt.AddSeconds(1)} WHERE source_result_id = {earliestRequested.SourceResultId}", TestContext.Current.CancellationToken);
                await normalize.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_freshness_expiry_causes SET cause_id = {lowerCauseIdWinner}, due_at = {dueAt.AddSeconds(1)}, requested_at = {requestedAt.AddSeconds(2)} WHERE source_result_id = {lowerCauseIdTie.SourceResultId}", TestContext.Current.CancellationToken);
                await normalize.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_freshness_expiry_causes SET cause_id = {remainingCauseId}, due_at = {dueAt.AddSeconds(1)}, requested_at = {requestedAt.AddSeconds(2)} WHERE source_result_id = {higherCauseIdTie.SourceResultId}", TestContext.Current.CancellationToken);
            }
            finally
            {
                await normalize.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_causes ENABLE TRIGGER tr_probe_freshness_expiry_causes_append_only", TestContext.Current.CancellationToken);
            }

            var expectedCauseIds = new[] { dueAtWinnerCauseId, requestedAtWinnerCauseId, lowerCauseIdWinner };
            var processor = new ProbeFreshnessExpiryCauseProcessor(normalize);
            foreach (var expectedCauseId in expectedCauseIds)
            {
                var outcome = await processor.ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
                Assert.Equal(expectedCauseId, outcome.CauseId);
            }
        }
        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(3, await verify.ProbeFreshnessExpiryCauseDispositions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.ProbeFreshnessExpiryCauseDispositions.Where(row => row.CauseId == remainingCauseId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.ProbeFreshnessExpiryCauses.Where(row => !verify.ProbeFreshnessExpiryCauseDispositions.Any(disposition => disposition.CauseId == row.CauseId)).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10NotYetDueReturnsTheExactEmptyOutcomeWithoutMutation()
    {
        await using var fixture = await CreateFixtureAsync();
        var future = DateTimeOffset.UtcNow.AddDays(1);
        await AddLedgerAsync(fixture, future, future, 3, 0m);
        await using (var results = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(results, new FixedClock(future)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var expiry = new EePulseDbContext(fixture.Options))
            Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.NoDueCause),
                await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Empty(await verify.ProbeFreshnessExpiryCauseDispositions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.ProbeFreshnessExpiryCauseTransitions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProbeStatus.Up, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).VisibleStatus);
    }

    [Fact]
    public async Task St10ConcurrentExpiryCallsSerializeAndTheWaiterRereadsTheCommittedDisposition()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var results = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var blocker = new BlockingSaveInterceptor();
        var blockedOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(blocker).Options;
        await using var firstDb = new EePulseDbContext(blockedOptions);
        await using var secondDb = new EePulseDbContext(fixture.Options);
        var first = new ProbeFreshnessExpiryCauseProcessor(firstDb).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await blocker.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = new ProbeFreshnessExpiryCauseProcessor(secondDb).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        blocker.Release.TrySetResult();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Contains(outcomes, outcome => outcome.Kind == ProbeFreshnessExpiryProcessorOutcomeKind.Applied);
        Assert.Contains(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.NoDueCause), outcomes);
        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Single(await verify.ProbeFreshnessExpiryCauseDispositions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.ProbeFreshnessExpiryCauseTransitions.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10LedgerContenderBehindExpiryLockCommitsAfterTheCutoffDecision()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var results = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var blocker = new BlockingSaveInterceptor();
        var expiryOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(blocker).Options;
        await using var expiryDb = new EePulseDbContext(expiryOptions);
        var expiry = new ProbeFreshnessExpiryCauseProcessor(expiryDb).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await blocker.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await using var ingestionDb = new EePulseDbContext(fixture.Options);
        await using var ingestionTransaction = await ingestionDb.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var backendProcessId = await ingestionDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(TestContext.Current.CancellationToken);
        var waitingLock = ProbeTransactionLock.AcquireAsync(ingestionDb, fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var observer = new NpgsqlConnection(fixture.ConnectionString))
        {
            await observer.OpenAsync(TestContext.Current.CancellationToken);
            await WaitForUngrantedProbeLockAsync(observer, backendProcessId, fixture.ProbeId, waitingLock, TestContext.Current.CancellationToken);
        }

        blocker.Release.TrySetResult();
        Assert.Equal(ProbeFreshnessExpiryProcessorOutcomeKind.Applied, (await expiry).Kind);
        await waitingLock;
        var laterResultId = Guid.NewGuid();
        ingestionDb.Add(CreateLedger(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m, 1, laterResultId, 1m));
        await ingestionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        await ingestionTransaction.CommitAsync(TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Single(await verify.ProbeFreshnessExpiryCauseDispositions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProbeStatus.Unknown, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).VisibleStatus);
        Assert.Null(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.ResultId == laterResultId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10FinalFlushFailureRollsBackTheDrainedResultAndExpiryDecision()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var initial = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(initial, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var pendingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m, configurationVersion: 99);
        var baseline = await CaptureSt10RollbackSnapshotAsync(fixture);
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ThrowOnSecondSaveInterceptor()).Options;
        await using (var failing = new EePulseDbContext(failingOptions))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeFreshnessExpiryCauseProcessor(failing).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        await using (var failed = new EePulseDbContext(fixture.Options))
        {
            var afterFailure = await CaptureSt10RollbackSnapshotAsync(fixture);
            Assert.Equal(baseline.Projection, afterFailure.Projection);
            Assert.True(baseline.ResultDispositions.SequenceEqual(afterFailure.ResultDispositions));
            Assert.True(baseline.ResultTransitions.SequenceEqual(afterFailure.ResultTransitions));
            Assert.True(baseline.Incidents.SequenceEqual(afterFailure.Incidents));
            Assert.True(baseline.EventIds.SequenceEqual(afterFailure.EventIds));
            Assert.True(baseline.ContextIds.SequenceEqual(afterFailure.ContextIds));
            Assert.True(baseline.Causes.SequenceEqual(afterFailure.Causes));
            Assert.True(baseline.ExpiryDispositions.SequenceEqual(afterFailure.ExpiryDispositions));
            Assert.True(baseline.ExpiryTransitions.SequenceEqual(afterFailure.ExpiryTransitions));
            Assert.NotNull(await failed.ProbeResultLedgerEntries.AsNoTracking().SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == pendingResultId, TestContext.Current.CancellationToken));
            Assert.Null(await failed.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == pendingResultId, TestContext.Current.CancellationToken));
            Assert.Single(await failed.ProbeFreshnessExpiryCauses.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await failed.ProbeFreshnessExpiryCauseDispositions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await failed.ProbeFreshnessExpiryCauseTransitions.ToListAsync(TestContext.Current.CancellationToken));
        }
        await using (var retry = new EePulseDbContext(fixture.Options))
            await new ProbeFreshnessExpiryCauseProcessor(retry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var afterRetry = new EePulseDbContext(fixture.Options))
        {
            Assert.Single(await afterRetry.ProbeResultProcessingDispositions.Where(row => row.AgentId == fixture.AgentId && row.ResultId == pendingResultId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Single(await afterRetry.ProbeFreshnessExpiryCauseDispositions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Single(await afterRetry.ProbeFreshnessExpiryCauseTransitions.ToListAsync(TestContext.Current.CancellationToken));
        }
        await using (var replay = new EePulseDbContext(fixture.Options))
            Assert.Equal(new ProbeFreshnessExpiryProcessorOutcome(ProbeFreshnessExpiryProcessorOutcomeKind.NoDueCause),
                await new ProbeFreshnessExpiryCauseProcessor(replay).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        await using var afterReplay = new EePulseDbContext(fixture.Options);
        Assert.Single(await afterReplay.ProbeResultProcessingDispositions.Where(row => row.AgentId == fixture.AgentId && row.ResultId == pendingResultId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await afterReplay.ProbeFreshnessExpiryCauseDispositions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await afterReplay.ProbeFreshnessExpiryCauseTransitions.ToListAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<OccurrenceRollbackSnapshot> CaptureOccurrenceRollbackSnapshotAsync(Fixture fixture)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var projection = await db.ProbeStatusProjections.AsNoTracking().SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        var incidents = await db.AvailabilityIncidents.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new IncidentSnapshot(row.Id, row.Status, row.OccurrenceCount)).ToArrayAsync(TestContext.Current.CancellationToken);
        var dispositionIds = await db.ProbeResultProcessingDispositions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId)
            .Select(row => new ResultIdentity(row.AgentId, row.ResultId)).ToArrayAsync(TestContext.Current.CancellationToken);
        var transitionIds = await db.ProbeResultStatusTransitions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId)
            .Select(row => new ResultIdentity(row.AgentId, row.ResultId)).ToArrayAsync(TestContext.Current.CancellationToken);
        var eventIds = await db.IncidentLifecycleEvents.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        var contextIds = await db.NotificationSuppressionContexts.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        return new(projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount,
            projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId,
            projection.StateVersion, projection.OpenIncidentId, incidents, dispositionIds, transitionIds, eventIds, contextIds);
    }

    private static async Task<St09cRollbackSnapshot> CaptureSt09cRollbackSnapshotAsync(Fixture fixture)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var projection = await db.ProbeStatusProjections.AsNoTracking().SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        var dispositions = await db.ProbeResultProcessingDispositions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId)
            .Select(row => new ResultIdentity(row.AgentId, row.ResultId)).ToArrayAsync(TestContext.Current.CancellationToken);
        var transitions = await db.ProbeResultStatusTransitions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId)
            .Select(row => new ResultIdentity(row.AgentId, row.ResultId)).ToArrayAsync(TestContext.Current.CancellationToken);
        var incidents = await db.AvailabilityIncidents.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new IncidentStateSnapshot(row.Id, row.Status, row.OpenedAt, row.AcknowledgedAt, row.AcknowledgedBy,
                row.AcknowledgementComment, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote, row.OccurrenceCount)).ToArrayAsync(TestContext.Current.CancellationToken);
        var events = await db.IncidentLifecycleEvents.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        var contexts = await db.NotificationSuppressionContexts.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        var causes = await db.ProbeFreshnessExpiryCauses.AsNoTracking().OrderBy(row => row.SourceAgentId).ThenBy(row => row.SourceResultId)
            .Select(row => new FreshnessCauseIdentity(row.SourceAgentId, row.SourceResultId, row.ProbeId, row.SourceCursorEventAt)).ToArrayAsync(TestContext.Current.CancellationToken);
        return new(new(projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount,
                projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId,
                projection.StateVersion, projection.OpenIncidentId), dispositions, transitions, incidents, events, contexts, causes);
    }

    private static async Task<St10RollbackSnapshot> CaptureSt10RollbackSnapshotAsync(Fixture fixture)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var projection = await db.ProbeStatusProjections.AsNoTracking().Where(row => row.ProbeId == fixture.ProbeId)
            .Select(row => new St10ProjectionSnapshot(row.ProbeId, row.UnderlyingStatus, row.VisibleStatus, row.ConsecutiveFailureCount,
                row.ConsecutiveSuccessCount, row.LastFreshEventAt, row.WatermarkEventAt, row.WatermarkAgentId,
                row.WatermarkResultId, row.StateVersion, row.OpenIncidentId)).SingleAsync(TestContext.Current.CancellationToken);
        var resultDispositions = await db.ProbeResultProcessingDispositions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId)
            .Select(row => new St10ResultDispositionSnapshot(row.AgentId, row.ResultId, row.ProbeId, row.EventAt,
                row.Disposition, row.ReasonCode, row.ResolvedPolicySnapshotId, row.ResolvedPolicyVersion, row.DecidedAt))
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var resultTransitions = await db.ProbeResultStatusTransitions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId)
            .Select(row => new St10ResultTransitionSnapshot(row.AgentId, row.ResultId, row.ProbeId, row.FromStatus,
                row.ToStatus, row.ReasonCode, row.EventAt, row.ReceivedAt, row.ProcessingDisposition))
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var incidents = await db.AvailabilityIncidents.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new IncidentStateSnapshot(row.Id, row.Status, row.OpenedAt, row.AcknowledgedAt, row.AcknowledgedBy,
                row.AcknowledgementComment, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote, row.OccurrenceCount)).ToArrayAsync(TestContext.Current.CancellationToken);
        var eventIds = await db.IncidentLifecycleEvents.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        var contextIds = await db.NotificationSuppressionContexts.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        var causes = await db.ProbeFreshnessExpiryCauses.AsNoTracking().OrderBy(row => row.CauseId)
            .Select(row => new St10CauseSnapshot(row.CauseId, row.ProbeId, row.SourceAgentId, row.SourceResultId,
                row.SourceCursorEventAt, row.SourceLastFreshEventAt, row.SourceConfigurationVersion, row.SourceAgentGroupId,
                row.PolicySnapshotId, row.PolicyVersion, row.FreshnessIntervalSeconds, row.FreshnessGraceSeconds, row.DueAt, row.RequestedAt))
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var expiryDispositions = await db.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().OrderBy(row => row.CauseId)
            .Select(row => new St10ExpiryDispositionSnapshot(row.CauseId, row.ProbeId, row.PolicySnapshotId, row.PolicyVersion,
                row.Outcome, row.ReasonCode, row.ExpiryCutoffReceivedAt, row.AppliedAt)).ToArrayAsync(TestContext.Current.CancellationToken);
        var expiryTransitions = await db.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().OrderBy(row => row.CauseId)
            .Select(row => new St10ExpiryTransitionSnapshot(row.CauseId, row.ProbeId, row.PolicySnapshotId, row.PolicyVersion,
                row.DispositionOutcome, row.FromVisibleStatus, row.ToVisibleStatus, row.ReasonCode, row.AppliedAt)).ToArrayAsync(TestContext.Current.CancellationToken);
        return new(projection, resultDispositions, resultTransitions, incidents, eventIds, contextIds, causes, expiryDispositions, expiryTransitions);
    }

    private static async Task<St10ProjectionSnapshot> ReadSt10ProjectionAsync(Fixture fixture)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.ProbeStatusProjections.AsNoTracking().Where(row => row.ProbeId == fixture.ProbeId)
            .Select(row => new St10ProjectionSnapshot(row.ProbeId, row.UnderlyingStatus, row.VisibleStatus,
                row.ConsecutiveFailureCount, row.ConsecutiveSuccessCount, row.LastFreshEventAt,
                row.WatermarkEventAt, row.WatermarkAgentId, row.WatermarkResultId,
                row.StateVersion, row.OpenIncidentId)).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<H1ProjectionSnapshot> ReadH1ProjectionAsync(Fixture fixture)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.ProbeStatusProjections.AsNoTracking().Where(row => row.ProbeId == fixture.ProbeId)
            .Select(row => new H1ProjectionSnapshot(row.ProbeId, row.UnderlyingStatus, row.VisibleStatus,
                row.ConsecutiveFailureCount, row.ConsecutiveSuccessCount, row.StateVersion, row.WatermarkAgentId,
                row.WatermarkResultId, row.WatermarkEventAt, row.LastFreshEventAt, row.OpenIncidentId))
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<DueHeartbeatCause> CreateDueAuthoritativeH1CauseAsync(Fixture fixture, ProbeStatus status)
    {
        await using (var empty = new EePulseDbContext(fixture.Options))
            Assert.Empty(await empty.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).ToArrayAsync(TestContext.Current.CancellationToken));
        var oldHeartbeat = fixture.Now.AddMinutes(-2);
        var events = status switch
        {
            ProbeStatus.Up => new[] { (fixture.Now, 3, 0m, 1m) },
            ProbeStatus.Degraded => new[] { (fixture.Now, 3, 0m, 500m) },
            ProbeStatus.Down => new[] { (fixture.Now, 0, 0m, 1m), (fixture.Now.AddSeconds(1), 0, 0m, 1m) },
            ProbeStatus.Recovering => new[] { (fixture.Now, 0, 0m, 1m), (fixture.Now.AddSeconds(1), 0, 0m, 1m), (fixture.Now.AddSeconds(2), 3, 0m, 1m) },
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        Guid resultId = Guid.Empty;
        DateTimeOffset eventAt = default;
        Guid? expectedOpenIncidentId = null;
        for (var index = 0; index < events.Length; index++)
        {
            if (index == events.Length - 1)
            {
                await using var predecessors = new EePulseDbContext(fixture.Options);
                Assert.Empty(await predecessors.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).ToArrayAsync(TestContext.Current.CancellationToken));
                await SetHeartbeatAsync(fixture, fixture.AgentId, oldHeartbeat);
            }
            resultId = Guid.Parse($"40000000-0000-0000-0000-{((int)status + 1):D12}");
            if (index != events.Length - 1) resultId = Guid.NewGuid();
            eventAt = events[index].Item1;
            await AddLedgerAsync(fixture, eventAt, eventAt, events[index].Item2, events[index].Item3, resultId: resultId, averageRtt: events[index].Item4);
            var createdBefore = index == events.Length - 1 ? await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken) : default;
            await using var processorContext = new EePulseDbContext(fixture.Options);
            await new ProbeResultStatusProcessor(processorContext, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            if (status == ProbeStatus.Recovering && index == 1)
            {
                await using var incidentContext = new EePulseDbContext(fixture.Options);
                var incident = await incidentContext.AvailabilityIncidents.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId && x.Status == AvailabilityIncidentStatus.Open, TestContext.Current.CancellationToken);
                expectedOpenIncidentId = incident.Id;
            }
            if (index == events.Length - 1)
            {
                var createdAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
                await using var createdContext = new EePulseDbContext(fixture.Options);
                var created = await createdContext.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
                Assert.InRange(created.RequestedAt, createdBefore, createdAfter);
                Assert.Equal((fixture.AgentId, resultId, fixture.ProbeId, oldHeartbeat, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, oldHeartbeat.AddSeconds(60)), (created.AuthorityAgentId, created.SourceResultId, created.ProbeId, created.SourceLastHeartbeatReceivedAt, created.SourceHeartbeatIntervalSeconds, created.SourceConfigurationVersion, created.SourceAgentGroupId, created.PolicySnapshotId, created.PolicyVersion, created.DueAt));
            }
        }
        await using var verify = new EePulseDbContext(fixture.Options);
        var cause = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceResultId == resultId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        if (status is ProbeStatus.Up or ProbeStatus.Degraded)
        {
            Assert.False(await verify.AvailabilityIncidents.AsNoTracking().AnyAsync(x => x.ProbeId == fixture.ProbeId && x.Status == AvailabilityIncidentStatus.Open, TestContext.Current.CancellationToken));
        }
        else if (status == ProbeStatus.Down)
        {
            var incident = await verify.AvailabilityIncidents.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId && x.Status == AvailabilityIncidentStatus.Open, TestContext.Current.CancellationToken);
            expectedOpenIncidentId = incident.Id;
        }
        else
        {
            var incident = await verify.AvailabilityIncidents.AsNoTracking().SingleAsync(x => x.Id == expectedOpenIncidentId && x.ProbeId == fixture.ProbeId && x.Status == AvailabilityIncidentStatus.Open, TestContext.Current.CancellationToken);
            Assert.Equal(expectedOpenIncidentId, incident.Id);
        }
        Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, eventAt), (cause.CauseType, cause.SourceDisposition, cause.SourceCursorEventAt));
        var dueInvocationBound = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.True(cause.DueAt <= dueInvocationBound);
        Assert.Equal(expectedOpenIncidentId, projection.OpenIncidentId);
        return new(cause.CauseId, resultId, eventAt, cause.DueAt, expectedOpenIncidentId);
    }

    private static async Task<H1NoMutationSnapshot> CaptureH1NoMutationSnapshotAsync(Fixture fixture)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var projection = await db.ProbeStatusProjections.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId)
            .Select(x => new St10ProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus, x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.LastFreshEventAt, x.WatermarkEventAt, x.WatermarkAgentId, x.WatermarkResultId, x.StateVersion, x.OpenIncidentId)).SingleOrDefaultAsync(TestContext.Current.CancellationToken);
        var ledger = (await db.ProbeResultLedgerEntries.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).OrderBy(x => x.AgentId).ThenBy(x => x.ResultId).ToArrayAsync(TestContext.Current.CancellationToken)).Select(x => new H1LedgerFullSnapshot(x.AgentId, x.ResultId, x.ProbeId, x.ConfigurationVersion, x.StartedAt, x.EndedAt, x.AttemptCount, x.SuccessfulAttemptCount, x.PacketLossRatio, x.MinRttMilliseconds, x.AverageRttMilliseconds, x.MaxRttMilliseconds, x.ErrorCategory, Convert.ToHexString(x.ImmutablePayloadDigest), x.ReceivedAt)).ToArray();
        var resultDispositions = await db.ProbeResultProcessingDispositions.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).OrderBy(x => x.AgentId).ThenBy(x => x.ResultId).Select(x => new St10ResultDispositionSnapshot(x.AgentId, x.ResultId, x.ProbeId, x.EventAt, x.Disposition, x.ReasonCode, x.ResolvedPolicySnapshotId, x.ResolvedPolicyVersion, x.DecidedAt)).ToArrayAsync(TestContext.Current.CancellationToken);
        var resultTransitions = await db.ProbeResultStatusTransitions.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).OrderBy(x => x.AgentId).ThenBy(x => x.ResultId).Select(x => new St10ResultTransitionSnapshot(x.AgentId, x.ResultId, x.ProbeId, x.FromStatus, x.ToStatus, x.ReasonCode, x.EventAt, x.ReceivedAt, x.ProcessingDisposition)).ToArrayAsync(TestContext.Current.CancellationToken);
        var freshness = await db.ProbeFreshnessExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).OrderBy(x => x.CauseId).Select(x => new FreshnessFullSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.SourceAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastFreshEventAt, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.FreshnessIntervalSeconds, x.FreshnessGraceSeconds, x.DueAt, x.RequestedAt)).ToArrayAsync(TestContext.Current.CancellationToken);
        var heartbeat = await db.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).OrderBy(x => x.CauseId).Select(x => new HeartbeatFullSnapshot(x.CauseId, x.ProbeId, x.CauseType, x.AuthorityAgentId, x.SourceResultId, x.SourceCursorEventAt, x.SourceLastHeartbeatReceivedAt, x.SourceHeartbeatIntervalSeconds, x.SourceConfigurationVersion, x.SourceAgentGroupId, x.SourceDisposition, x.PolicySnapshotId, x.PolicyVersion, x.DueAt, x.RequestedAt)).ToArrayAsync(TestContext.Current.CancellationToken);
        var dispositions = await db.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).OrderBy(x => x.CauseId).Select(x => new HeartbeatDispositionFullSnapshot(x.CauseId, x.ProbeId, x.PolicySnapshotId, x.PolicyVersion, x.Outcome, x.ReasonCode, x.ExpiryCutoffReceivedAt, x.AppliedAt)).ToArrayAsync(TestContext.Current.CancellationToken);
        var transitions = await db.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).OrderBy(x => x.CauseId).Select(x => new HeartbeatTransitionFullSnapshot(x.CauseId, x.ProbeId, x.PolicySnapshotId, x.PolicyVersion, x.DispositionOutcome, x.FromVisibleStatus, x.ToVisibleStatus, x.ReasonCode, x.AppliedAt)).ToArrayAsync(TestContext.Current.CancellationToken);
        return new(projection, ledger, resultDispositions, resultTransitions, freshness, heartbeat, dispositions, transitions, await ReadProbeArtifactsAsync(fixture));
    }

    private static void AssertH1NoMutationSnapshotEqual(H1NoMutationSnapshot expected, H1NoMutationSnapshot actual)
    {
        Assert.Equal(expected.Projection, actual.Projection); Assert.True(expected.Ledger.SequenceEqual(actual.Ledger)); Assert.True(expected.ResultDispositions.SequenceEqual(actual.ResultDispositions)); Assert.True(expected.ResultTransitions.SequenceEqual(actual.ResultTransitions)); Assert.True(expected.FreshnessCauses.SequenceEqual(actual.FreshnessCauses)); Assert.True(expected.HeartbeatCauses.SequenceEqual(actual.HeartbeatCauses)); Assert.True(expected.Dispositions.SequenceEqual(actual.Dispositions)); Assert.True(expected.Transitions.SequenceEqual(actual.Transitions)); Assert.True(expected.Artifacts.Incidents.SequenceEqual(actual.Artifacts.Incidents)); Assert.True(expected.Artifacts.Events.SequenceEqual(actual.Artifacts.Events)); Assert.True(expected.Artifacts.Contexts.SequenceEqual(actual.Artifacts.Contexts));
    }

    private static void AssertH1NoOpDelta(H1NoMutationSnapshot baseline, H1NoMutationSnapshot post, Guid causeId, Guid probeId, Guid policyId, string reasonCode, DateTimeOffset cutoff)
    {
        Assert.Equal(baseline.Projection, post.Projection); Assert.True(baseline.Ledger.SequenceEqual(post.Ledger)); Assert.True(baseline.ResultDispositions.SequenceEqual(post.ResultDispositions)); Assert.True(baseline.ResultTransitions.SequenceEqual(post.ResultTransitions)); Assert.True(baseline.FreshnessCauses.SequenceEqual(post.FreshnessCauses)); Assert.True(baseline.HeartbeatCauses.SequenceEqual(post.HeartbeatCauses)); Assert.True(baseline.Transitions.SequenceEqual(post.Transitions)); Assert.True(baseline.Artifacts.Incidents.SequenceEqual(post.Artifacts.Incidents)); Assert.True(baseline.Artifacts.Events.SequenceEqual(post.Artifacts.Events)); Assert.True(baseline.Artifacts.Contexts.SequenceEqual(post.Artifacts.Contexts));
        var expected = baseline.Dispositions.Append(new HeartbeatDispositionFullSnapshot(causeId, probeId, policyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, reasonCode, cutoff, null)).OrderBy(x => x.CauseId).ToArray();
        Assert.True(expected.SequenceEqual(post.Dispositions));
    }

    private static async Task<ProbeResultProcessingDisposition?> FindDispositionAsync(Fixture fixture, Guid resultId)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.ProbeResultProcessingDispositions.AsNoTracking().SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
    }

    private static async Task<ProbeResultStatusTransition?> FindTransitionAsync(Fixture fixture, Guid resultId)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.ProbeResultStatusTransitions.AsNoTracking().SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
    }

    private static void AssertOccurrenceRollbackSnapshotEqual(OccurrenceRollbackSnapshot expected, OccurrenceRollbackSnapshot actual)
    {
        Assert.Equal((expected.UnderlyingStatus, expected.ConsecutiveFailureCount, expected.ConsecutiveSuccessCount,
                expected.LastFreshEventAt, expected.WatermarkEventAt, expected.WatermarkAgentId, expected.WatermarkResultId,
                expected.StateVersion, expected.OpenIncidentId),
            (actual.UnderlyingStatus, actual.ConsecutiveFailureCount, actual.ConsecutiveSuccessCount,
                actual.LastFreshEventAt, actual.WatermarkEventAt, actual.WatermarkAgentId, actual.WatermarkResultId,
                actual.StateVersion, actual.OpenIncidentId));
        Assert.Equal(expected.Incidents, actual.Incidents);
        Assert.Equal(expected.Dispositions, actual.Dispositions);
        Assert.Equal(expected.Transitions, actual.Transitions);
        Assert.Equal(expected.EventIds, actual.EventIds);
        Assert.Equal(expected.ContextIds, actual.ContextIds);
    }

    private static async Task SetSchedulingOwnerEnabledAsync(Fixture fixture, string owner, bool enabled)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        switch (owner)
        {
            case "probe":
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probes SET enabled = {enabled} WHERE id = {fixture.ProbeId}", TestContext.Current.CancellationToken);
                break;
            case "device":
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE devices SET enabled = {enabled} WHERE id = (SELECT device_id FROM probes WHERE id = {fixture.ProbeId})", TestContext.Current.CancellationToken);
                break;
            case "agent-group":
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_groups SET enabled = {enabled} WHERE id = {fixture.GroupId}", TestContext.Current.CancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(owner));
        }
    }

    private static async Task AssertDisabledWinsOverTimeRuleAsync(TimeSpan eventOffset, string overriddenReason)
    {
        await using var fixture = await CreateFixtureAsync();
        await SetSchedulingOwnerEnabledAsync(fixture, "probe", false);
        var resultId = await AddLedgerAsync(fixture, fixture.Now.Add(eventOffset), fixture.Now, 3, 0m);
        await using (var processing = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        var disposition = (await FindDispositionAsync(fixture, resultId))!;
        Assert.Equal((ProbeResultProcessingDispositionKind.Disabled, "disabled"), (disposition.Disposition, disposition.ReasonCode));
        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Empty(await verify.ProbeFreshnessExpiryCauses.AsNoTracking().Where(cause =>
            cause.SourceAgentId == fixture.AgentId && cause.SourceResultId == resultId && cause.ProbeId == fixture.ProbeId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(overriddenReason, disposition.ReasonCode);
    }

    private static async Task<DisabledNonMutationSnapshot> CaptureDisabledNonMutationSnapshotAsync(Fixture fixture)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var projection = await db.ProbeStatusProjections.AsNoTracking().Where(row => row.ProbeId == fixture.ProbeId)
            .Select(row => new ProjectionSnapshot(row.UnderlyingStatus, row.ConsecutiveFailureCount, row.ConsecutiveSuccessCount,
                row.LastFreshEventAt, row.WatermarkEventAt, row.WatermarkAgentId, row.WatermarkResultId, row.StateVersion, row.OpenIncidentId))
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
        var incidents = await db.AvailabilityIncidents.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new IncidentStateSnapshot(row.Id, row.Status, row.OpenedAt, row.AcknowledgedAt, row.AcknowledgedBy,
                row.AcknowledgementComment, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote, row.OccurrenceCount))
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var transitions = await db.ProbeResultStatusTransitions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId)
            .Select(row => new ResultIdentity(row.AgentId, row.ResultId)).ToArrayAsync(TestContext.Current.CancellationToken);
        var events = await db.IncidentLifecycleEvents.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        var contexts = await db.NotificationSuppressionContexts.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        return new(projection, Serialize(incidents), Serialize(transitions), Serialize(events), Serialize(contexts));
    }

    private static string Serialize<T>(IEnumerable<T> values) => string.Join("|", values.Select(value => value!.ToString()));

    [Fact]
    public async Task T2ResultProcessingUsesStableLockedSourceForFreshnessAndHeartbeatCauseLineage()
    {
        await using var fixture = await CreateFixtureAsync();
        var heartbeatAt = fixture.Now.AddMinutes(1);
        var agentB = await AddSecondAgentAsync(fixture, heartbeatAt);
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m, agentId: agentB);
        await using (var processing = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultId, (await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        await using var verify = new EePulseDbContext(fixture.Options);
        var disposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(x => x.AgentId == agentB && x.ResultId == resultId, TestContext.Current.CancellationToken);
        var freshness = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultId, TestContext.Current.CancellationToken);
        var heartbeat = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.AuthorityAgentId == agentB && x.SourceResultId == resultId, TestContext.Current.CancellationToken);
        Assert.Equal((ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.Now, fixture.PolicyId, 1), (disposition.Disposition, disposition.ProbeId, disposition.EventAt, disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion));
        Assert.NotEqual(Guid.Empty, freshness.CauseId); Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentB, resultId, fixture.Now, fixture.Now, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60), (freshness.CauseType, freshness.SourceDisposition, freshness.ProbeId, freshness.SourceAgentId, freshness.SourceResultId, freshness.SourceCursorEventAt, freshness.SourceLastFreshEventAt, freshness.SourceConfigurationVersion, freshness.SourceAgentGroupId, freshness.PolicySnapshotId, freshness.PolicyVersion, freshness.FreshnessIntervalSeconds, freshness.FreshnessGraceSeconds)); Assert.Equal(fixture.Now.AddSeconds(60), freshness.DueAt); Assert.NotEqual(default, freshness.RequestedAt);
        Assert.NotEqual(Guid.Empty, heartbeat.CauseId); Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentB, resultId, fixture.Now, heartbeatAt, 20, 1L, fixture.GroupId, fixture.PolicyId, 1), (heartbeat.CauseType, heartbeat.SourceDisposition, heartbeat.ProbeId, heartbeat.AuthorityAgentId, heartbeat.SourceResultId, heartbeat.SourceCursorEventAt, heartbeat.SourceLastHeartbeatReceivedAt, heartbeat.SourceHeartbeatIntervalSeconds, heartbeat.SourceConfigurationVersion, heartbeat.SourceAgentGroupId, heartbeat.PolicySnapshotId, heartbeat.PolicyVersion)); Assert.Equal(heartbeatAt.AddSeconds(60), heartbeat.DueAt); Assert.NotEqual(default, heartbeat.RequestedAt);
        Assert.False(await verify.ProbeFreshnessExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceResultId == resultId && x.SourceAgentId == fixture.AgentId, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceResultId == resultId && x.AuthorityAgentId == fixture.AgentId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task T2ResultProcessingRetriesWhenAdvisorySourceIsReplacedBeforeProbeStabilization()
    {
        await using var fixture = await CreateFixtureAsync(); var agentB = await AddSecondAgentAsync(fixture, fixture.Now.AddMinutes(1));
        var heartbeatA = fixture.Now.AddMinutes(2); await using (var seedA = new EePulseDbContext(fixture.Options)) { var agent = await seedA.Agents.SingleAsync(x => x.Id == fixture.AgentId, TestContext.Current.CancellationToken); agent.Heartbeat("1.0.0", "processor", 0, AgentSelfHealth.Healthy, 1, heartbeatA, heartbeatA); await seedA.SaveChangesAsync(TestContext.Current.CancellationToken); }
        var resultA = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);
        var beforeOrder = await ReadPendingOrderAsync(fixture); Assert.Equal(new[] { new LedgerOrder(fixture.AgentId, resultA, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1)) }, beforeOrder);
        var baselineArtifacts = await ReadProbeArtifactsAsync(fixture);
        var gate = new FirstAgentShareGate(); var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(gate).Options;
        var firstBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken); await using var processing = new EePulseDbContext(options); var invocation = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken); Exception? primary = null;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var resultB = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m, agentId: agentB);
            Assert.Equal(new[] { new LedgerOrder(agentB, resultB, fixture.Now, fixture.Now), new LedgerOrder(fixture.AgentId, resultA, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1)) }, await ReadPendingOrderAsync(fixture));
            gate.Release.TrySetResult();
            var first = await invocation; var firstAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken); Assert.Equal(resultB, first.ResultId);
            await using (var afterFirst = new EePulseDbContext(fixture.Options))
            {
                Assert.False(await afterFirst.ProbeResultProcessingDispositions.AsNoTracking().AnyAsync(x => x.AgentId == fixture.AgentId && x.ResultId == resultA, TestContext.Current.CancellationToken));
                Assert.False(await afterFirst.ProbeResultStatusTransitions.AsNoTracking().AnyAsync(x => x.AgentId == fixture.AgentId && x.ResultId == resultA, TestContext.Current.CancellationToken));
                var currentArtifacts = await ReadProbeArtifactsAsync(fixture); var newIncidents = currentArtifacts.Incidents.Except(baselineArtifacts.Incidents).ToArray(); var newEvents = currentArtifacts.Events.Except(baselineArtifacts.Events).ToArray(); var newContexts = currentArtifacts.Contexts.Except(baselineArtifacts.Contexts).ToArray(); Assert.Empty(newIncidents); Assert.All(newEvents, x => Assert.Equal((agentB, resultB), (x.SourceAgentId, x.SourceResultId))); Assert.All(newContexts, x => Assert.Contains(x.EventId, newEvents.Select(e => e.EventId))); Assert.True(baselineArtifacts.Incidents.SequenceEqual(currentArtifacts.Incidents)); Assert.True(baselineArtifacts.Events.SequenceEqual(currentArtifacts.Events)); Assert.True(baselineArtifacts.Contexts.SequenceEqual(currentArtifacts.Contexts)); Assert.False(await afterFirst.IncidentLifecycleEvents.AsNoTracking().AnyAsync(x => x.SourceAgentId == fixture.AgentId && x.SourceResultId == resultA, TestContext.Current.CancellationToken));
                Assert.False(await afterFirst.ProbeFreshnessExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceResultId == resultA, TestContext.Current.CancellationToken));
                Assert.False(await afterFirst.ProbeHeartbeatExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceResultId == resultA, TestContext.Current.CancellationToken));
                var projection = await afterFirst.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken); Assert.Equal((agentB, resultB), (projection.WatermarkAgentId, projection.WatermarkResultId));
            }
            var secondBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken); await using var second = new EePulseDbContext(fixture.Options); Assert.Equal(resultA, (await new ProbeResultStatusProcessor(second, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId); var secondAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
            await using var verify = new EePulseDbContext(fixture.Options);
            foreach (var source in new[] { (AgentId: fixture.AgentId, ResultId: resultA, EventAt: fixture.Now.AddSeconds(1), HeartbeatAt: heartbeatA), (AgentId: agentB, ResultId: resultB, EventAt: fixture.Now, HeartbeatAt: fixture.Now.AddMinutes(1)) })
            {
                var disposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(x => x.AgentId == source.AgentId && x.ResultId == source.ResultId, TestContext.Current.CancellationToken); Assert.Equal((fixture.ProbeId, source.EventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1), (disposition.ProbeId, disposition.EventAt, disposition.Disposition, disposition.ReasonCode, disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion));
                var freshness = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceAgentId == source.AgentId && x.SourceResultId == source.ResultId, TestContext.Current.CancellationToken); Assert.NotEqual(Guid.Empty, freshness.CauseId); Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, source.EventAt, source.EventAt, source.AgentId, source.ResultId, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60, source.EventAt.AddSeconds(60)), (freshness.CauseType, freshness.SourceDisposition, freshness.ProbeId, freshness.SourceCursorEventAt, freshness.SourceLastFreshEventAt, freshness.SourceAgentId, freshness.SourceResultId, freshness.SourceConfigurationVersion, freshness.SourceAgentGroupId, freshness.PolicySnapshotId, freshness.PolicyVersion, freshness.FreshnessIntervalSeconds, freshness.FreshnessGraceSeconds, freshness.DueAt)); Assert.InRange(freshness.RequestedAt, source.AgentId == agentB ? firstBefore : secondBefore, source.AgentId == agentB ? firstAfter : secondAfter);
                var heartbeat = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.AuthorityAgentId == source.AgentId && x.SourceResultId == source.ResultId, TestContext.Current.CancellationToken); Assert.NotEqual(Guid.Empty, heartbeat.CauseId); Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, source.EventAt, source.AgentId, source.ResultId, source.HeartbeatAt, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, source.HeartbeatAt.AddSeconds(60)), (heartbeat.CauseType, heartbeat.SourceDisposition, heartbeat.ProbeId, heartbeat.SourceCursorEventAt, heartbeat.AuthorityAgentId, heartbeat.SourceResultId, heartbeat.SourceLastHeartbeatReceivedAt, heartbeat.SourceHeartbeatIntervalSeconds, heartbeat.SourceConfigurationVersion, heartbeat.SourceAgentGroupId, heartbeat.PolicySnapshotId, heartbeat.PolicyVersion, heartbeat.DueAt)); Assert.InRange(heartbeat.RequestedAt, source.AgentId == agentB ? firstBefore : secondBefore, source.AgentId == agentB ? firstAfter : secondAfter);
            }
            var freshnessCauses = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().Where(x => x.SourceResultId == resultA || x.SourceResultId == resultB).ToArrayAsync(TestContext.Current.CancellationToken); Assert.Equal(2, freshnessCauses.Select(x => x.CauseId).Distinct().Count()); Assert.Equal(2, freshnessCauses.Select(x => (x.SourceAgentId, x.SourceResultId)).Distinct().Count()); var causes = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.SourceResultId == resultA || x.SourceResultId == resultB).ToArrayAsync(TestContext.Current.CancellationToken); Assert.Equal(2, causes.Select(x => x.CauseId).Distinct().Count()); Assert.Equal(2, causes.Select(x => (x.AuthorityAgentId, x.SourceResultId)).Distinct().Count());
        }
        catch (Exception exception) { primary = exception; throw; }
        finally { gate.Release.TrySetResult(); try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await invocation.WaitAsync(cleanup.Token); } catch (Exception cleanupFailure) when (primary is not null) { primary.Data["T2AgentShareGateCleanupFailure"] = cleanupFailure; } }
    }

    [Fact]
    public async Task T2FreshnessExpiryDrainsPreCutoffResultsFromMultipleAgentsBeforeChoosingDueCause()
    {
        await using var fixture = await CreateFixtureAsync();
        var heartbeatA = fixture.Now;
        var heartbeatB = fixture.Now.AddMinutes(1);
        var agentB = await AddSecondAgentAsync(fixture, heartbeatB);
        await SetHeartbeatAsync(fixture, fixture.AgentId, heartbeatA);

        var resultA = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var initialBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using (var initial = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultA, (await new ProbeResultStatusProcessor(initial, new FixedClock(fixture.Now))
                .ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        var initialAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Guid aCauseId;
        await using (var initialVerify = new EePulseDbContext(fixture.Options))
        {
            var initialCause = await initialVerify.ProbeFreshnessExpiryCauses.AsNoTracking()
                .SingleAsync(x => x.SourceAgentId == fixture.AgentId && x.SourceResultId == resultA,
                    TestContext.Current.CancellationToken);
            aCauseId = initialCause.CauseId;
            Assert.InRange(initialCause.RequestedAt, initialBefore, initialAfter);
        }

        var resultBAt = fixture.Now.AddSeconds(1);
        var resultB = await AddLedgerAsync(fixture, resultBAt, resultBAt, 3, 0m, agentId: agentB);
        Assert.Equal(new[] { new LedgerOrder(agentB, resultB, resultBAt, resultBAt) }, await ReadPendingOrderAsync(fixture));

        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.True(resultBAt <= before);
        await using (var eligibility = new EePulseDbContext(fixture.Options))
        {
            var preexistingCause = await eligibility.ProbeFreshnessExpiryCauses.AsNoTracking()
                .SingleAsync(x => x.CauseId == aCauseId, TestContext.Current.CancellationToken);
            Assert.True(preexistingCause.DueAt <= before);
        }
        ProbeFreshnessExpiryProcessorOutcome outcome;
        await using (var expiry = new EePulseDbContext(fixture.Options))
            outcome = await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var bDisposition = await verify.ProbeResultProcessingDispositions.AsNoTracking()
            .SingleAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken);
        var bFresh = await verify.ProbeFreshnessExpiryCauses.AsNoTracking()
            .SingleAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken);
        var bHeartbeat = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking()
            .SingleAsync(x => x.AuthorityAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken);
        var aCause = await verify.ProbeFreshnessExpiryCauses.AsNoTracking()
            .SingleAsync(x => x.SourceAgentId == fixture.AgentId && x.SourceResultId == resultA, TestContext.Current.CancellationToken);
        var noOp = await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking()
            .SingleAsync(x => x.CauseId == aCause.CauseId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.AsNoTracking()
            .SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);

        Assert.Equal((ProbeFreshnessExpiryProcessorOutcomeKind.NoOp, aCause.CauseId,
                ProbeFreshnessExpiryCauseDispositionOutcome.NoOp,
                ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode),
            (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
        Assert.Equal((fixture.ProbeId, agentB, resultB, resultBAt,
                ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1),
            (bDisposition.ProbeId, bDisposition.AgentId, bDisposition.ResultId, bDisposition.EventAt,
                bDisposition.Disposition, bDisposition.ReasonCode, bDisposition.ResolvedPolicySnapshotId,
                bDisposition.ResolvedPolicyVersion));
        Assert.InRange(bDisposition.DecidedAt, before, after);
        Assert.NotEqual(Guid.Empty, bFresh.CauseId);
        Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry,
                ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentB, resultB,
                resultBAt, resultBAt, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60,
                resultBAt.AddSeconds(60)),
            (bFresh.CauseType, bFresh.SourceDisposition, bFresh.ProbeId, bFresh.SourceAgentId,
                bFresh.SourceResultId, bFresh.SourceCursorEventAt, bFresh.SourceLastFreshEventAt,
                bFresh.SourceConfigurationVersion, bFresh.SourceAgentGroupId, bFresh.PolicySnapshotId,
                bFresh.PolicyVersion, bFresh.FreshnessIntervalSeconds, bFresh.FreshnessGraceSeconds,
                bFresh.DueAt));
        Assert.InRange(bFresh.RequestedAt, before, after);
        Assert.NotEqual(Guid.Empty, bHeartbeat.CauseId);
        Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry,
                ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentB, resultB,
                resultBAt, heartbeatB, 20, 1L, fixture.GroupId, fixture.PolicyId, 1,
                heartbeatB.AddSeconds(60)),
            (bHeartbeat.CauseType, bHeartbeat.SourceDisposition, bHeartbeat.ProbeId,
                bHeartbeat.AuthorityAgentId, bHeartbeat.SourceResultId, bHeartbeat.SourceCursorEventAt,
                bHeartbeat.SourceLastHeartbeatReceivedAt, bHeartbeat.SourceHeartbeatIntervalSeconds,
                bHeartbeat.SourceConfigurationVersion, bHeartbeat.SourceAgentGroupId,
                bHeartbeat.PolicySnapshotId, bHeartbeat.PolicyVersion, bHeartbeat.DueAt));
        Assert.InRange(bHeartbeat.RequestedAt, before, after);
        Assert.NotEqual(Guid.Empty, aCause.CauseId);
        Assert.Equal(aCauseId, aCause.CauseId);
        Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry,
                ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, resultA,
                fixture.Now, fixture.Now, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60,
                fixture.Now.AddSeconds(60)),
            (aCause.CauseType, aCause.SourceDisposition, aCause.ProbeId, aCause.SourceAgentId,
                aCause.SourceResultId, aCause.SourceCursorEventAt, aCause.SourceLastFreshEventAt,
                aCause.SourceConfigurationVersion, aCause.SourceAgentGroupId, aCause.PolicySnapshotId,
                aCause.PolicyVersion, aCause.FreshnessIntervalSeconds, aCause.FreshnessGraceSeconds,
                aCause.DueAt));
        Assert.NotEqual(default, aCause.RequestedAt);
        Assert.Equal((aCause.CauseId, fixture.ProbeId, fixture.PolicyId, 1,
                ProbeFreshnessExpiryCauseDispositionOutcome.NoOp,
                ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode),
            (noOp.CauseId, noOp.ProbeId, noOp.PolicySnapshotId, noOp.PolicyVersion,
                noOp.Outcome, noOp.ReasonCode));
        Assert.InRange(noOp.ExpiryCutoffReceivedAt, before, after);
        Assert.Equal(bDisposition.DecidedAt, noOp.ExpiryCutoffReceivedAt);
        Assert.Null(noOp.AppliedAt);
        Assert.Equal((agentB, resultB, resultBAt, resultBAt),
            (projection.WatermarkAgentId, projection.WatermarkResultId,
                projection.WatermarkEventAt, projection.LastFreshEventAt));
        Assert.Empty(await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking()
            .Where(x => x.CauseId == aCause.CauseId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking()
            .CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeFreshnessExpiryCauses.AsNoTracking()
            .AnyAsync(x => x.SourceResultId == resultB && x.SourceAgentId != agentB, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeHeartbeatExpiryCauses.AsNoTracking()
            .AnyAsync(x => x.SourceResultId == resultB && x.AuthorityAgentId != agentB, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task T2HeartbeatExpiryDrainsPreCutoffResultsFromMultipleAuthoritiesBeforeChoosingDueCause()
    {
        await using var fixture = await CreateFixtureAsync();
        var heartbeatA = fixture.Now;
        var heartbeatB = fixture.Now.AddMinutes(1);
        var agentB = await AddSecondAgentAsync(fixture, heartbeatB);
        await SetHeartbeatAsync(fixture, fixture.AgentId, heartbeatA);

        var resultA = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var initialBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using (var initial = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultA, (await new ProbeResultStatusProcessor(initial, new FixedClock(fixture.Now))
                .ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        var initialAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Guid aCauseId;
        await using (var initialVerify = new EePulseDbContext(fixture.Options))
        {
            var initialCause = await initialVerify.ProbeHeartbeatExpiryCauses.AsNoTracking()
                .SingleAsync(x => x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == resultA,
                    TestContext.Current.CancellationToken);
            aCauseId = initialCause.CauseId;
            Assert.InRange(initialCause.RequestedAt, initialBefore, initialAfter);
        }

        var resultBAt = fixture.Now.AddSeconds(1);
        var resultB = await AddLedgerAsync(fixture, resultBAt, resultBAt, 3, 0m, agentId: agentB);
        Assert.Equal(new[] { new LedgerOrder(agentB, resultB, resultBAt, resultBAt) }, await ReadPendingOrderAsync(fixture));

        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.True(resultBAt <= before);
        await using (var eligibility = new EePulseDbContext(fixture.Options))
        {
            var preexistingCause = await eligibility.ProbeHeartbeatExpiryCauses.AsNoTracking()
                .SingleAsync(x => x.CauseId == aCauseId, TestContext.Current.CancellationToken);
            Assert.True(preexistingCause.DueAt <= before);
        }
        ProbeHeartbeatExpiryProcessorOutcome outcome;
        await using (var expiry = new EePulseDbContext(fixture.Options))
            outcome = await new ProbeHeartbeatExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var bDisposition = await verify.ProbeResultProcessingDispositions.AsNoTracking()
            .SingleAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken);
        var bFresh = await verify.ProbeFreshnessExpiryCauses.AsNoTracking()
            .SingleAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken);
        var bHeartbeat = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking()
            .SingleAsync(x => x.AuthorityAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken);
        var aCause = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking()
            .SingleAsync(x => x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == resultA, TestContext.Current.CancellationToken);
        var selected = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking()
            .SingleAsync(x => x.CauseId == aCause.CauseId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.AsNoTracking()
            .SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);

        Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, aCause.CauseId,
                ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp,
                ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode),
            (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
        Assert.Equal((fixture.ProbeId, agentB, resultB, resultBAt,
                ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1),
            (bDisposition.ProbeId, bDisposition.AgentId, bDisposition.ResultId, bDisposition.EventAt,
                bDisposition.Disposition, bDisposition.ReasonCode, bDisposition.ResolvedPolicySnapshotId,
                bDisposition.ResolvedPolicyVersion));
        Assert.InRange(bDisposition.DecidedAt, before, after);
        Assert.NotEqual(Guid.Empty, bFresh.CauseId);
        Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry,
                ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentB, resultB,
                resultBAt, resultBAt, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60,
                resultBAt.AddSeconds(60)),
            (bFresh.CauseType, bFresh.SourceDisposition, bFresh.ProbeId, bFresh.SourceAgentId,
                bFresh.SourceResultId, bFresh.SourceCursorEventAt, bFresh.SourceLastFreshEventAt,
                bFresh.SourceConfigurationVersion, bFresh.SourceAgentGroupId, bFresh.PolicySnapshotId,
                bFresh.PolicyVersion, bFresh.FreshnessIntervalSeconds, bFresh.FreshnessGraceSeconds,
                bFresh.DueAt));
        Assert.InRange(bFresh.RequestedAt, before, after);
        Assert.NotEqual(Guid.Empty, bHeartbeat.CauseId);
        Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry,
                ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentB, resultB,
                resultBAt, heartbeatB, 20, 1L, fixture.GroupId, fixture.PolicyId, 1,
                heartbeatB.AddSeconds(60)),
            (bHeartbeat.CauseType, bHeartbeat.SourceDisposition, bHeartbeat.ProbeId,
                bHeartbeat.AuthorityAgentId, bHeartbeat.SourceResultId, bHeartbeat.SourceCursorEventAt,
                bHeartbeat.SourceLastHeartbeatReceivedAt, bHeartbeat.SourceHeartbeatIntervalSeconds,
                bHeartbeat.SourceConfigurationVersion, bHeartbeat.SourceAgentGroupId,
                bHeartbeat.PolicySnapshotId, bHeartbeat.PolicyVersion, bHeartbeat.DueAt));
        Assert.InRange(bHeartbeat.RequestedAt, before, after);
        Assert.NotEqual(Guid.Empty, aCause.CauseId);
        Assert.Equal(aCauseId, aCause.CauseId);
        Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry,
                ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, resultA,
                fixture.Now, heartbeatA, 20, 1L, fixture.GroupId, fixture.PolicyId, 1,
                heartbeatA.AddSeconds(60)),
            (aCause.CauseType, aCause.SourceDisposition, aCause.ProbeId, aCause.AuthorityAgentId,
                aCause.SourceResultId, aCause.SourceCursorEventAt, aCause.SourceLastHeartbeatReceivedAt,
                aCause.SourceHeartbeatIntervalSeconds, aCause.SourceConfigurationVersion,
                aCause.SourceAgentGroupId, aCause.PolicySnapshotId, aCause.PolicyVersion, aCause.DueAt));
        Assert.NotEqual(default, aCause.RequestedAt);
        Assert.Equal((aCause.CauseId, fixture.ProbeId, fixture.PolicyId, 1,
                ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp,
                ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode),
            (selected.CauseId, selected.ProbeId, selected.PolicySnapshotId, selected.PolicyVersion,
                selected.Outcome, selected.ReasonCode));
        Assert.InRange(selected.ExpiryCutoffReceivedAt, before, after);
        Assert.Equal(bDisposition.DecidedAt, selected.ExpiryCutoffReceivedAt);
        Assert.Null(selected.AppliedAt);
        Assert.Equal((agentB, resultB, resultBAt, resultBAt),
            (projection.WatermarkAgentId, projection.WatermarkResultId,
                projection.WatermarkEventAt, projection.LastFreshEventAt));
        Assert.Empty(await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking()
            .Where(x => x.CauseId == aCause.CauseId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking()
            .CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeFreshnessExpiryCauses.AsNoTracking()
            .AnyAsync(x => x.SourceResultId == resultB && x.SourceAgentId != agentB, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeHeartbeatExpiryCauses.AsNoTracking()
            .AnyAsync(x => x.SourceResultId == resultB && x.AuthorityAgentId != agentB, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T4A1ExpiryCoordinatorsAcquireCanonicalMultiAgentLocksBeforeProbeLock(bool heartbeat)
    {
        await using var fixture = await CreateFixtureAsync();
        var otherAgent = await AddSecondAgentAsync(fixture, fixture.Now);
        var agents = new[] { fixture.AgentId, otherAgent }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        var agentLow = agents[0]; var agentHigh = agents[1];
        var seededOrder = new[] { agentHigh, agentLow };
        Assert.True(agentLow.ToString("D").CompareTo(agentHigh.ToString("D"), StringComparison.Ordinal) < 0); Assert.False(seededOrder.SequenceEqual(agents));
        var heartbeatLow = (await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken)).AddMinutes(-4);
        var heartbeatHigh = heartbeatLow.AddTicks(10);
        foreach (var seed in new[] { (AgentId: agentHigh, Heartbeat: heartbeatHigh), (AgentId: agentLow, Heartbeat: heartbeatLow) }) await SetHeartbeatAsync(fixture, seed.AgentId, seed.Heartbeat);
        var resultLow = Guid.Parse(heartbeat ? "90000000-0000-0000-0000-000000000001" : "91000000-0000-0000-0000-000000000001");
        var resultHigh = Guid.Parse(heartbeat ? "90000000-0000-0000-0000-000000000002" : "91000000-0000-0000-0000-000000000002");
        var eventLow = fixture.Now; var eventHigh = fixture.Now.AddSeconds(1);
        await AddLedgerAsync(fixture, eventLow, eventLow, 3, 0m, resultId: resultLow, agentId: agentLow);
        await using (var initial = new EePulseDbContext(fixture.Options)) Assert.Equal(resultLow, (await new ProbeResultStatusProcessor(initial, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        await AddLedgerAsync(fixture, eventHigh, eventHigh, 3, 0m, resultId: resultHigh, agentId: agentHigh);
        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        var expiryDispositionCountBefore = 0;
        await using (var precondition = new EePulseDbContext(fixture.Options))
        {
            var controlledDue = heartbeat
                ? await precondition.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.AuthorityAgentId == agentLow && x.SourceResultId == resultLow, TestContext.Current.CancellationToken)
                : null;
            var controlledFresh = heartbeat ? null : await precondition.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceAgentId == agentLow && x.SourceResultId == resultLow, TestContext.Current.CancellationToken);
            Assert.True((heartbeat ? controlledDue!.DueAt : controlledFresh!.DueAt) <= before);
            expiryDispositionCountBefore = heartbeat
                ? await precondition.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)
                : await precondition.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal(0, expiryDispositionCountBefore);
            Assert.Equal(new[] { new LedgerOrder(agentHigh, resultHigh, eventHigh, eventHigh) }, await ReadPendingOrderAsync(fixture));
            var required = await precondition.ProbeResultLedgerEntries.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !precondition.ProbeResultProcessingDispositions.Any(d => d.AgentId == x.AgentId && d.ResultId == x.ResultId)).Select(x => x.AgentId).Concat(heartbeat ? precondition.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !precondition.ProbeHeartbeatExpiryCauseDispositions.Any(d => d.CauseId == x.CauseId)).Select(x => x.AuthorityAgentId) : precondition.ProbeFreshnessExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !precondition.ProbeFreshnessExpiryCauseDispositions.Any(d => d.CauseId == x.CauseId)).Select(x => x.SourceAgentId)).Distinct().ToArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal(agents, required.OrderBy(id => id.ToString("D"), StringComparer.Ordinal));
        }

        var applicationName = $"t4a1-{Guid.NewGuid():N}"; var processorConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = applicationName }.ConnectionString;
        await using var blockerA = new NpgsqlConnection(fixture.ConnectionString); await blockerA.OpenAsync(TestContext.Current.CancellationToken); await using var txA = await blockerA.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);
        var pidA = await LockAgentForUpdateAsync(blockerA, txA, agentHigh, TestContext.Current.CancellationToken);
        await using var observer = new NpgsqlConnection(fixture.ConnectionString); await observer.OpenAsync(TestContext.Current.CancellationToken);
        var blockerC = new NpgsqlConnection(fixture.ConnectionString); await blockerC.OpenAsync(TestContext.Current.CancellationToken);
        var txC = await blockerC.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);
        var pidC = await GetBackendPidAsync(blockerC, TestContext.Current.CancellationToken);
        Task? invocation = null; Task? waitC = null; NpgsqlCommand? waitCCommand = null; CancellationTokenSource? waitCCancellation = null; Exception? primary = null; int? pidB = null; var releasedA = false; var releasedC = false;
        ProbeHeartbeatExpiryProcessorOutcome? heartbeatOutcome = null; ProbeFreshnessExpiryProcessorOutcome? freshnessOutcome = null;
        try
        {
            await using var processor = new EePulseDbContext(new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(processorConnectionString).Options);
            invocation = heartbeat
                ? RunHeartbeatAsync(processor, fixture.ProbeId, value => heartbeatOutcome = value, TestContext.Current.CancellationToken)
                : RunFreshnessAsync(processor, fixture.ProbeId, value => freshnessOutcome = value, TestContext.Current.CancellationToken);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); bounded.CancelAfter(TimeSpan.FromSeconds(10));
            pidB = await WaitForBackendPidAsync(observer, applicationName, invocation, TestContext.Current.CancellationToken);
            Assert.NotEqual(pidA, pidB.Value);
            await WaitForTransactionLockEvidenceAsync(observer, pidB.Value, pidA, requireNoAdvisoryLock: true, invocation, TestContext.Current.CancellationToken);
            Assert.NotEqual(pidA, pidC); Assert.NotEqual(pidB.Value, pidC);
            waitCCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            waitCCancellation.CancelAfter(TimeSpan.FromSeconds(10));
            waitC = LockAgentForUpdateWithoutFollowUpCommandAsync(blockerC, txC, agentLow, command => waitCCommand = command, waitCCancellation.Token);
            await WaitForTransactionLockEvidenceAsync(observer, pidC, pidB.Value, requireNoAdvisoryLock: false, waitC, TestContext.Current.CancellationToken);
            await txA.RollbackAsync(TestContext.Current.CancellationToken);
            releasedA = true;
            await invocation.WaitAsync(bounded.Token);
            await waitC.WaitAsync(bounded.Token);
            await txC.RollbackAsync(TestContext.Current.CancellationToken);
            releasedC = true;
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            var cleanupFailures = new List<Exception>();
            async Task AttemptCleanupAsync(string name, Func<CancellationToken, Task> action)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await action(cleanup.Token);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(new InvalidOperationException($"T4A1 cleanup failed while {name}.", cleanupFailure));
                }
            }

            if (!releasedA) await AttemptCleanupAsync("releasing blocker A", async cancellationToken => await txA.RollbackAsync(cancellationToken));
            if (invocation is not null && !await ObserveTerminalAsync("the processor invocation", invocation, expectedCancellation: false, cleanupFailures))
            {
                if (pidB is not null) await AttemptCleanupAsync("canceling processor B's backend", async cancellationToken => await SignalBackendAsync(observer, pidB.Value, terminate: false, cancellationToken));
                if (!await ObserveTerminalAsync("the processor invocation after backend cancellation", invocation, expectedCancellation: true, cleanupFailures) && pidB is not null)
                {
                    await AttemptCleanupAsync("terminating processor B's backend", async cancellationToken => await SignalBackendAsync(observer, pidB.Value, terminate: true, cancellationToken));
                    await ObserveTerminalAsync("the processor invocation after backend termination", invocation, expectedCancellation: true, cleanupFailures);
                }
            }
            if (waitC is not null && !await ObserveTerminalAsync("blocker C's FOR UPDATE command", waitC, expectedCancellation: false, cleanupFailures))
            {
                try { waitCCancellation?.Cancel(); }
                catch (Exception cleanupFailure) { cleanupFailures.Add(new InvalidOperationException("T4A1 cleanup failed while canceling blocker C's command token.", cleanupFailure)); }
                try { waitCCommand?.Cancel(); }
                catch (Exception cleanupFailure) { cleanupFailures.Add(new InvalidOperationException("T4A1 cleanup failed while invoking blocker C command cancellation.", cleanupFailure)); }
                if (!await ObserveTerminalAsync("blocker C's command after token cancellation", waitC, expectedCancellation: true, cleanupFailures))
                {
                    await AttemptCleanupAsync("canceling blocker C's backend", async cancellationToken => await SignalBackendAsync(observer, pidC, terminate: false, cancellationToken));
                    if (!await ObserveTerminalAsync("blocker C's command after backend cancellation", waitC, expectedCancellation: true, cleanupFailures))
                    {
                        await AttemptCleanupAsync("terminating blocker C's backend", async cancellationToken => await SignalBackendAsync(observer, pidC, terminate: true, cancellationToken));
                        await ObserveTerminalAsync("blocker C's command after backend termination", waitC, expectedCancellation: true, cleanupFailures);
                    }
                }
            }
            if (waitC is null || waitC.IsCompleted)
            {
                if (!releasedC) await AttemptCleanupAsync("releasing blocker C", async cancellationToken => await txC.RollbackAsync(cancellationToken));
                await AttemptCleanupAsync("disposing blocker C's transaction", async _ => await txC.DisposeAsync());
                await AttemptCleanupAsync("disposing blocker C's connection", async _ => await blockerC.DisposeAsync());
            }
            else cleanupFailures.Add(new InvalidOperationException("T4A1 cleanup could not dispose blocker C because its FOR UPDATE command did not reach a terminal state."));
            waitCCancellation?.Dispose();

            if (cleanupFailures.Count == 0) { }
            else if (primary is not null)
            {
                for (var index = 0; index < cleanupFailures.Count; index++) primary.Data[$"T4A1CleanupFailure{index + 1}"] = cleanupFailures[index];
            }
            else if (cleanupFailures.Count == 1) throw cleanupFailures[0];
            else throw new AggregateException(cleanupFailures);
        }

        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using var verify = new EePulseDbContext(fixture.Options);
        var highDisposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(x => x.AgentId == agentHigh && x.ResultId == resultHigh, TestContext.Current.CancellationToken);
        var highFresh = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceAgentId == agentHigh && x.SourceResultId == resultHigh, TestContext.Current.CancellationToken);
        var highHeartbeat = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.AuthorityAgentId == agentHigh && x.SourceResultId == resultHigh, TestContext.Current.CancellationToken);
        Assert.Equal((agentHigh, resultHigh, fixture.ProbeId, eventHigh, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1), (highDisposition.AgentId, highDisposition.ResultId, highDisposition.ProbeId, highDisposition.EventAt, highDisposition.Disposition, highDisposition.ReasonCode, highDisposition.ResolvedPolicySnapshotId, highDisposition.ResolvedPolicyVersion));
        Assert.InRange(highDisposition.DecidedAt, before, after);
        Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentHigh, resultHigh, eventHigh, eventHigh, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60, eventHigh.AddSeconds(60)), (highFresh.CauseType, highFresh.SourceDisposition, highFresh.ProbeId, highFresh.SourceAgentId, highFresh.SourceResultId, highFresh.SourceCursorEventAt, highFresh.SourceLastFreshEventAt, highFresh.SourceConfigurationVersion, highFresh.SourceAgentGroupId, highFresh.PolicySnapshotId, highFresh.PolicyVersion, highFresh.FreshnessIntervalSeconds, highFresh.FreshnessGraceSeconds, highFresh.DueAt));
        Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentHigh, resultHigh, eventHigh, heartbeatHigh, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, heartbeatHigh.AddSeconds(60)), (highHeartbeat.CauseType, highHeartbeat.SourceDisposition, highHeartbeat.ProbeId, highHeartbeat.AuthorityAgentId, highHeartbeat.SourceResultId, highHeartbeat.SourceCursorEventAt, highHeartbeat.SourceLastHeartbeatReceivedAt, highHeartbeat.SourceHeartbeatIntervalSeconds, highHeartbeat.SourceConfigurationVersion, highHeartbeat.SourceAgentGroupId, highHeartbeat.PolicySnapshotId, highHeartbeat.PolicyVersion, highHeartbeat.DueAt));
        Assert.InRange(highFresh.RequestedAt, before, after); Assert.InRange(highHeartbeat.RequestedAt, before, after);
        var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal((agentHigh, resultHigh, eventHigh, eventHigh), (projection.WatermarkAgentId, projection.WatermarkResultId, projection.WatermarkEventAt, projection.LastFreshEventAt));
        if (heartbeat)
        {
            var cause = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.AuthorityAgentId == agentLow && x.SourceResultId == resultLow, TestContext.Current.CancellationToken); var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == cause.CauseId, TestContext.Current.CancellationToken);
            Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, cause.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode), (heartbeatOutcome!.Kind, heartbeatOutcome.CauseId, heartbeatOutcome.DispositionOutcome, heartbeatOutcome.ReasonCode)); Assert.Equal(ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode, disposition.ReasonCode); Assert.Equal(highDisposition.DecidedAt, disposition.ExpiryCutoffReceivedAt); Assert.Empty(await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().Where(x => x.CauseId == cause.CauseId).ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal((cause.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode, highDisposition.DecidedAt, (DateTimeOffset?)null), (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode, disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt));
            Assert.Equal(expiryDispositionCountBefore + 1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.False(await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == highHeartbeat.CauseId, TestContext.Current.CancellationToken));
            Assert.Empty(await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().Where(x => x.CauseId == cause.CauseId || x.CauseId == highHeartbeat.CauseId).ToArrayAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            var cause = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceAgentId == agentLow && x.SourceResultId == resultLow, TestContext.Current.CancellationToken); var disposition = await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == cause.CauseId, TestContext.Current.CancellationToken);
            Assert.Equal((ProbeFreshnessExpiryProcessorOutcomeKind.NoOp, cause.CauseId, ProbeFreshnessExpiryCauseDispositionOutcome.NoOp, ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode), (freshnessOutcome!.Kind, freshnessOutcome.CauseId, freshnessOutcome.DispositionOutcome, freshnessOutcome.ReasonCode)); Assert.Equal(ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode, disposition.ReasonCode); Assert.Equal(highDisposition.DecidedAt, disposition.ExpiryCutoffReceivedAt); Assert.Empty(await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().Where(x => x.CauseId == cause.CauseId).ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal((cause.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeFreshnessExpiryCauseDispositionOutcome.NoOp, ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode, highDisposition.DecidedAt, (DateTimeOffset?)null), (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode, disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt));
            Assert.Equal(expiryDispositionCountBefore + 1, await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.False(await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == highFresh.CauseId, TestContext.Current.CancellationToken));
            Assert.Empty(await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().Where(x => x.CauseId == cause.CauseId || x.CauseId == highFresh.CauseId).ToArrayAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(2, await verify.ProbeResultProcessingDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)); Assert.Equal(2, await verify.ProbeFreshnessExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)); Assert.Equal(2, await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)); Assert.Empty(await verify.AvailabilityIncidents.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task T2FreshnessExpiryLeavesPostCutoffResultPendingAndRecordsSingleDatabaseCutoff()
    {
        await using var fixture = await CreateFixtureAsync();
        var heartbeatA = fixture.Now;
        var heartbeatB = fixture.Now.AddMinutes(1);
        var agentB = await AddSecondAgentAsync(fixture, heartbeatB);
        await SetHeartbeatAsync(fixture, fixture.AgentId, heartbeatA);
        var resultA = Guid.Parse("10000000-0000-0000-0000-000000000001");
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m, resultId: resultA);

        var initialBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using (var initial = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultA, (await new ProbeResultStatusProcessor(initial, new FixedClock(fixture.Now))
                .ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        var initialAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        Guid causeId;
        await using (var initialVerify = new EePulseDbContext(fixture.Options))
        {
            var cause = await initialVerify.ProbeFreshnessExpiryCauses.AsNoTracking()
                .SingleAsync(x => x.SourceAgentId == fixture.AgentId && x.SourceResultId == resultA,
                    TestContext.Current.CancellationToken);
            causeId = cause.CauseId;
            Assert.InRange(cause.RequestedAt, initialBefore, initialAfter);
        }

        var futureAt = (await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken)).AddMinutes(5);
        var resultB = Guid.Parse("10000000-0000-0000-0000-000000000002");
        await AddLedgerAsync(fixture, futureAt, futureAt, 3, 0m, resultId: resultB, agentId: agentB);
        Assert.True(await ReadBoundaryAsync(fixture, agentB, 1) <= futureAt);
        Assert.Equal(new[] { new LedgerOrder(agentB, resultB, futureAt, futureAt) }, await ReadPendingOrderAsync(fixture));

        var projectionBefore = await ReadSt10ProjectionAsync(fixture);
        Assert.Equal((fixture.AgentId, resultA, fixture.Now, fixture.Now,
                ProbeStatus.Up, ProbeStatus.Up, 0, 1, 1L, (Guid?)null),
            (projectionBefore.WatermarkAgentId, projectionBefore.WatermarkResultId,
                projectionBefore.WatermarkEventAt, projectionBefore.LastFreshEventAt,
                projectionBefore.UnderlyingStatus, projectionBefore.VisibleStatus,
                projectionBefore.ConsecutiveFailureCount, projectionBefore.ConsecutiveSuccessCount,
                projectionBefore.StateVersion, projectionBefore.OpenIncidentId));
        var artifactsBefore = await ReadProbeArtifactsAsync(fixture);
        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.True(futureAt > before);
        await using (var eligibility = new EePulseDbContext(fixture.Options))
        {
            var cause = await eligibility.ProbeFreshnessExpiryCauses.AsNoTracking()
                .SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken);
            Assert.True(cause.DueAt <= before);
        }

        ProbeFreshnessExpiryProcessorOutcome outcome;
        await using (var expiry = new EePulseDbContext(fixture.Options))
            outcome = await new ProbeFreshnessExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var futureLedger = await verify.ProbeResultLedgerEntries.AsNoTracking()
            .SingleAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken);
        var cause = await verify.ProbeFreshnessExpiryCauses.AsNoTracking()
            .SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking()
            .SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken);
        var transition = await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking()
            .SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken);
        var projectionAfter = await ReadSt10ProjectionAsync(fixture);
        var artifactsAfter = await ReadProbeArtifactsAsync(fixture);

        Assert.Equal((ProbeFreshnessExpiryProcessorOutcomeKind.Applied, causeId,
                ProbeFreshnessExpiryCauseDispositionOutcome.Applied,
                ProbeFreshnessExpiryCauseDisposition.ResultFreshnessExpiredReasonCode),
            (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
        Assert.Equal((agentB, resultB, futureAt), (futureLedger.AgentId, futureLedger.ResultId, futureLedger.ReceivedAt));
        Assert.True(futureLedger.ReceivedAt > before);
        Assert.True(futureLedger.ReceivedAt > after);
        Assert.False(await verify.ProbeResultProcessingDispositions.AsNoTracking()
            .AnyAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeResultStatusTransitions.AsNoTracking()
            .AnyAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeFreshnessExpiryCauses.AsNoTracking()
            .AnyAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeHeartbeatExpiryCauses.AsNoTracking()
            .AnyAsync(x => x.AuthorityAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken));
        Assert.False(await verify.IncidentLifecycleEvents.AsNoTracking()
            .AnyAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken));
        Assert.Equal((causeId, fixture.ProbeId, fixture.AgentId, resultA, fixture.Now, fixture.Now,
                1L, fixture.GroupId, ProbeResultProcessingDispositionKind.StateDriving, fixture.PolicyId, 1,
                30, 60, fixture.Now.AddSeconds(60)),
            (cause.CauseId, cause.ProbeId, cause.SourceAgentId, cause.SourceResultId,
                cause.SourceCursorEventAt, cause.SourceLastFreshEventAt, cause.SourceConfigurationVersion,
                cause.SourceAgentGroupId, cause.SourceDisposition, cause.PolicySnapshotId, cause.PolicyVersion,
                cause.FreshnessIntervalSeconds, cause.FreshnessGraceSeconds, cause.DueAt));
        Assert.Equal((causeId, fixture.ProbeId, fixture.PolicyId, 1,
                ProbeFreshnessExpiryCauseDispositionOutcome.Applied,
                ProbeFreshnessExpiryCauseDisposition.ResultFreshnessExpiredReasonCode),
            (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion,
                disposition.Outcome, disposition.ReasonCode));
        Assert.InRange(disposition.ExpiryCutoffReceivedAt, before, after);
        Assert.True(futureLedger.ReceivedAt > disposition.ExpiryCutoffReceivedAt);
        Assert.Equal(disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt);
        Assert.Equal((causeId, fixture.ProbeId, fixture.PolicyId, 1,
                ProbeFreshnessExpiryCauseDispositionOutcome.Applied, ProbeStatus.Up, ProbeStatus.Unknown,
                ProbeFreshnessExpiryCauseTransition.ResultFreshnessExpiredReasonCode,
                disposition.ExpiryCutoffReceivedAt),
            (transition.CauseId, transition.ProbeId, transition.PolicySnapshotId, transition.PolicyVersion,
                transition.DispositionOutcome, transition.FromVisibleStatus, transition.ToVisibleStatus,
                transition.ReasonCode, transition.AppliedAt));
        Assert.Equal((projectionBefore.UnderlyingStatus, ProbeStatus.Unknown,
                projectionBefore.ConsecutiveFailureCount, projectionBefore.ConsecutiveSuccessCount,
                projectionBefore.LastFreshEventAt, projectionBefore.WatermarkEventAt,
                projectionBefore.WatermarkAgentId, projectionBefore.WatermarkResultId,
                projectionBefore.StateVersion + 1, projectionBefore.OpenIncidentId),
            (projectionAfter.UnderlyingStatus, projectionAfter.VisibleStatus,
                projectionAfter.ConsecutiveFailureCount, projectionAfter.ConsecutiveSuccessCount,
                projectionAfter.LastFreshEventAt, projectionAfter.WatermarkEventAt,
                projectionAfter.WatermarkAgentId, projectionAfter.WatermarkResultId,
                projectionAfter.StateVersion, projectionAfter.OpenIncidentId));
        Assert.True(artifactsBefore.Incidents.SequenceEqual(artifactsAfter.Incidents));
        Assert.True(artifactsBefore.Events.SequenceEqual(artifactsAfter.Events));
        Assert.True(artifactsBefore.Contexts.SequenceEqual(artifactsAfter.Contexts));
        Assert.Equal(1, await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking()
            .CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking()
            .CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task T2HeartbeatExpiryLeavesPostCutoffResultPendingAndRecordsSingleDatabaseCutoff()
    {
        await using var fixture = await CreateFixtureAsync();
        var heartbeatA = fixture.Now;
        var heartbeatB = fixture.Now.AddMinutes(1);
        var agentB = await AddSecondAgentAsync(fixture, heartbeatB);
        await SetHeartbeatAsync(fixture, fixture.AgentId, heartbeatA);
        var resultA = Guid.Parse("20000000-0000-0000-0000-000000000001");
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m, resultId: resultA);

        var initialBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using (var initial = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultA, (await new ProbeResultStatusProcessor(initial, new FixedClock(fixture.Now))
                .ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        var initialAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        Guid causeId;
        await using (var initialVerify = new EePulseDbContext(fixture.Options))
        {
            var cause = await initialVerify.ProbeHeartbeatExpiryCauses.AsNoTracking()
                .SingleAsync(x => x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == resultA,
                    TestContext.Current.CancellationToken);
            causeId = cause.CauseId;
            Assert.InRange(cause.RequestedAt, initialBefore, initialAfter);
        }

        var futureAt = (await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken)).AddMinutes(5);
        var resultB = Guid.Parse("20000000-0000-0000-0000-000000000002");
        await AddLedgerAsync(fixture, futureAt, futureAt, 3, 0m, resultId: resultB, agentId: agentB);
        Assert.True(await ReadBoundaryAsync(fixture, agentB, 1) <= futureAt);
        Assert.Equal(new[] { new LedgerOrder(agentB, resultB, futureAt, futureAt) }, await ReadPendingOrderAsync(fixture));

        var projectionBefore = await ReadSt10ProjectionAsync(fixture);
        Assert.Equal((fixture.AgentId, resultA, fixture.Now, fixture.Now,
                ProbeStatus.Up, ProbeStatus.Up, 0, 1, 1L, (Guid?)null),
            (projectionBefore.WatermarkAgentId, projectionBefore.WatermarkResultId,
                projectionBefore.WatermarkEventAt, projectionBefore.LastFreshEventAt,
                projectionBefore.UnderlyingStatus, projectionBefore.VisibleStatus,
                projectionBefore.ConsecutiveFailureCount, projectionBefore.ConsecutiveSuccessCount,
                projectionBefore.StateVersion, projectionBefore.OpenIncidentId));
        var artifactsBefore = await ReadProbeArtifactsAsync(fixture);
        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.True(futureAt > before);
        await using (var eligibility = new EePulseDbContext(fixture.Options))
        {
            var cause = await eligibility.ProbeHeartbeatExpiryCauses.AsNoTracking()
                .SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken);
            Assert.True(cause.DueAt <= before);
        }

        ProbeHeartbeatExpiryProcessorOutcome outcome;
        await using (var expiry = new EePulseDbContext(fixture.Options))
            outcome = await new ProbeHeartbeatExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var futureLedger = await verify.ProbeResultLedgerEntries.AsNoTracking()
            .SingleAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken);
        var cause = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking()
            .SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking()
            .SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken);
        var transition = await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking()
            .SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken);
        var projectionAfter = await ReadSt10ProjectionAsync(fixture);
        var artifactsAfter = await ReadProbeArtifactsAsync(fixture);

        Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.Applied, causeId,
                ProbeHeartbeatExpiryCauseDispositionOutcome.Applied,
                ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode),
            (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
        Assert.Equal((agentB, resultB, futureAt), (futureLedger.AgentId, futureLedger.ResultId, futureLedger.ReceivedAt));
        Assert.True(futureLedger.ReceivedAt > before);
        Assert.True(futureLedger.ReceivedAt > after);
        Assert.False(await verify.ProbeResultProcessingDispositions.AsNoTracking()
            .AnyAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeResultStatusTransitions.AsNoTracking()
            .AnyAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeFreshnessExpiryCauses.AsNoTracking()
            .AnyAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeHeartbeatExpiryCauses.AsNoTracking()
            .AnyAsync(x => x.AuthorityAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken));
        Assert.False(await verify.IncidentLifecycleEvents.AsNoTracking()
            .AnyAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken));
        Assert.Equal((causeId, fixture.ProbeId, fixture.AgentId, resultA, fixture.Now, heartbeatA,
                20, 1L, fixture.GroupId, ProbeResultProcessingDispositionKind.StateDriving, fixture.PolicyId, 1,
                heartbeatA.AddSeconds(60)),
            (cause.CauseId, cause.ProbeId, cause.AuthorityAgentId, cause.SourceResultId,
                cause.SourceCursorEventAt, cause.SourceLastHeartbeatReceivedAt,
                cause.SourceHeartbeatIntervalSeconds, cause.SourceConfigurationVersion,
                cause.SourceAgentGroupId, cause.SourceDisposition, cause.PolicySnapshotId, cause.PolicyVersion,
                cause.DueAt));
        Assert.Equal((causeId, fixture.ProbeId, fixture.PolicyId, 1,
                ProbeHeartbeatExpiryCauseDispositionOutcome.Applied,
                ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode),
            (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion,
                disposition.Outcome, disposition.ReasonCode));
        Assert.InRange(disposition.ExpiryCutoffReceivedAt, before, after);
        Assert.True(futureLedger.ReceivedAt > disposition.ExpiryCutoffReceivedAt);
        Assert.Equal(disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt);
        Assert.Equal((causeId, fixture.ProbeId, fixture.PolicyId, 1,
                ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeStatus.Up, ProbeStatus.Unknown,
                ProbeHeartbeatExpiryCauseTransition.AgentHeartbeatExpiredReasonCode,
                disposition.ExpiryCutoffReceivedAt),
            (transition.CauseId, transition.ProbeId, transition.PolicySnapshotId, transition.PolicyVersion,
                transition.DispositionOutcome, transition.FromVisibleStatus, transition.ToVisibleStatus,
                transition.ReasonCode, transition.AppliedAt));
        Assert.Equal((projectionBefore.UnderlyingStatus, ProbeStatus.Unknown,
                projectionBefore.ConsecutiveFailureCount, projectionBefore.ConsecutiveSuccessCount,
                projectionBefore.LastFreshEventAt, projectionBefore.WatermarkEventAt,
                projectionBefore.WatermarkAgentId, projectionBefore.WatermarkResultId,
                projectionBefore.StateVersion + 1, projectionBefore.OpenIncidentId),
            (projectionAfter.UnderlyingStatus, projectionAfter.VisibleStatus,
                projectionAfter.ConsecutiveFailureCount, projectionAfter.ConsecutiveSuccessCount,
                projectionAfter.LastFreshEventAt, projectionAfter.WatermarkEventAt,
                projectionAfter.WatermarkAgentId, projectionAfter.WatermarkResultId,
                projectionAfter.StateVersion, projectionAfter.OpenIncidentId));
        Assert.True(artifactsBefore.Incidents.SequenceEqual(artifactsAfter.Incidents));
        Assert.True(artifactsBefore.Events.SequenceEqual(artifactsAfter.Events));
        Assert.True(artifactsBefore.Contexts.SequenceEqual(artifactsAfter.Contexts));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking()
            .CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking()
            .CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10bH1NoDueCauseForNoCauseAndNotYetDueCauseLeavesNoMutation()
    {
        await using (var noCause = await CreateFixtureAsync())
        {
            var before = await CaptureH1NoMutationSnapshotAsync(noCause);
            await using var processorContext = new EePulseDbContext(noCause.Options);
            var outcome = await new ProbeHeartbeatExpiryCauseProcessor(processorContext)
                .ProcessNextDueAsync(noCause.ProbeId, TestContext.Current.CancellationToken);
            var after = await CaptureH1NoMutationSnapshotAsync(noCause);
            Assert.Equal(new ProbeHeartbeatExpiryProcessorOutcome(ProbeHeartbeatExpiryProcessorOutcomeKind.NoDueCause), outcome);
            AssertH1NoMutationSnapshotEqual(before, after);
        }

        await using var futureCause = await CreateFixtureAsync();
        var futureHeartbeat = (await ReadPostgresTimestampAsync(futureCause.ConnectionString, TestContext.Current.CancellationToken)).AddMinutes(5);
        await SetHeartbeatAsync(futureCause, futureCause.AgentId, futureHeartbeat);
        var resultId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        await AddLedgerAsync(futureCause, futureCause.Now, futureCause.Now, 3, 0m, resultId: resultId);
        var createdBefore = await ReadPostgresTimestampAsync(futureCause.ConnectionString, TestContext.Current.CancellationToken);
        await using (var results = new EePulseDbContext(futureCause.Options))
            Assert.Equal(resultId, (await new ProbeResultStatusProcessor(results, new FixedClock(futureCause.Now))
                .ProcessNextAsync(futureCause.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        var createdAfter = await ReadPostgresTimestampAsync(futureCause.ConnectionString, TestContext.Current.CancellationToken);
        Guid causeId;
        await using (var verifyCreated = new EePulseDbContext(futureCause.Options))
        {
            var cause = await verifyCreated.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceResultId == resultId, TestContext.Current.CancellationToken);
            causeId = cause.CauseId;
            Assert.InRange(cause.RequestedAt, createdBefore, createdAfter);
        }
        var invocationBefore = await ReadPostgresTimestampAsync(futureCause.ConnectionString, TestContext.Current.CancellationToken);
        await using (var eligibility = new EePulseDbContext(futureCause.Options))
            Assert.True((await eligibility.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken)).DueAt > invocationBefore);
        var baseline = await CaptureH1NoMutationSnapshotAsync(futureCause);
        await using (var processorContext = new EePulseDbContext(futureCause.Options))
            Assert.Equal(new ProbeHeartbeatExpiryProcessorOutcome(ProbeHeartbeatExpiryProcessorOutcomeKind.NoDueCause),
                await new ProbeHeartbeatExpiryCauseProcessor(processorContext).ProcessNextDueAsync(futureCause.ProbeId, TestContext.Current.CancellationToken));
        var post = await CaptureH1NoMutationSnapshotAsync(futureCause);
        AssertH1NoMutationSnapshotEqual(baseline, post);
        await using var final = new EePulseDbContext(futureCause.Options);
        Assert.False(await final.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken));
        Assert.False(await final.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().AnyAsync(x => x.CauseId == causeId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(H1KnownVisibleStatuses))]
    public async Task St10bH1AppliedTransitionsEveryKnownVisibleStatusToUnknown(ProbeStatus sourceStatus, int failures, int successes, long stateVersion)
    {
        await using var fixture = await CreateFixtureAsync();
        var due = await CreateDueAuthoritativeH1CauseAsync(fixture, sourceStatus);
        var projectionBefore = await ReadSt10ProjectionAsync(fixture);
        Assert.Equal((fixture.AgentId, due.ResultId, due.EventAt, due.EventAt, sourceStatus, sourceStatus, failures, successes, stateVersion),
            (projectionBefore.WatermarkAgentId, projectionBefore.WatermarkResultId, projectionBefore.WatermarkEventAt,
                projectionBefore.LastFreshEventAt, projectionBefore.UnderlyingStatus, projectionBefore.VisibleStatus,
                projectionBefore.ConsecutiveFailureCount, projectionBefore.ConsecutiveSuccessCount, projectionBefore.StateVersion));
        Assert.Equal(due.OpenIncidentId, projectionBefore.OpenIncidentId);
        var artifactsBefore = await ReadProbeArtifactsAsync(fixture);
        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.True(due.DueAt <= before);
        ProbeHeartbeatExpiryProcessorOutcome outcome;
        await using (var processorContext = new EePulseDbContext(fixture.Options))
            outcome = await new ProbeHeartbeatExpiryCauseProcessor(processorContext).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using var verify = new EePulseDbContext(fixture.Options);
        var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == due.CauseId, TestContext.Current.CancellationToken);
        var transition = await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().SingleAsync(x => x.CauseId == due.CauseId, TestContext.Current.CancellationToken);
        var projectionAfter = await ReadSt10ProjectionAsync(fixture);
        var artifactsAfter = await ReadProbeArtifactsAsync(fixture);
        Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.Applied, due.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode), (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
        Assert.Equal((due.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode), (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode));
        Assert.InRange(disposition.ExpiryCutoffReceivedAt, before, after); Assert.Equal(disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt);
        Assert.Equal((due.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, sourceStatus, ProbeStatus.Unknown, ProbeHeartbeatExpiryCauseTransition.AgentHeartbeatExpiredReasonCode, disposition.ExpiryCutoffReceivedAt), (transition.CauseId, transition.ProbeId, transition.PolicySnapshotId, transition.PolicyVersion, transition.DispositionOutcome, transition.FromVisibleStatus, transition.ToVisibleStatus, transition.ReasonCode, transition.AppliedAt));
        Assert.Equal((projectionBefore.UnderlyingStatus, ProbeStatus.Unknown, projectionBefore.ConsecutiveFailureCount, projectionBefore.ConsecutiveSuccessCount, projectionBefore.LastFreshEventAt, projectionBefore.WatermarkEventAt, projectionBefore.WatermarkAgentId, projectionBefore.WatermarkResultId, projectionBefore.StateVersion + 1, projectionBefore.OpenIncidentId), (projectionAfter.UnderlyingStatus, projectionAfter.VisibleStatus, projectionAfter.ConsecutiveFailureCount, projectionAfter.ConsecutiveSuccessCount, projectionAfter.LastFreshEventAt, projectionAfter.WatermarkEventAt, projectionAfter.WatermarkAgentId, projectionAfter.WatermarkResultId, projectionAfter.StateVersion, projectionAfter.OpenIncidentId));
        Assert.True(artifactsBefore.Incidents.SequenceEqual(artifactsAfter.Incidents)); Assert.True(artifactsBefore.Events.SequenceEqual(artifactsAfter.Events)); Assert.True(artifactsBefore.Contexts.SequenceEqual(artifactsAfter.Contexts));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(H1NamedNoOps))]
    public async Task St10bH1NamedNoOpsPersistExactDispositionWithoutStateMutation(string prerequisite, string reasonCode)
    {
        await using var fixture = await CreateFixtureAsync();
        var controlled = await CreateDueAuthoritativeH1CauseAsync(fixture, ProbeStatus.Up);
        var originalHeartbeatAt = fixture.Now.AddMinutes(-2);
        const int heartbeatIntervalSeconds = 20;
        var preMutationProjection = await ReadH1ProjectionAsync(fixture);
        var evidence = new H1NoOpPrerequisiteEvidence(
            new(controlled.CauseId, fixture.AgentId, controlled.ResultId, controlled.EventAt, originalHeartbeatAt,
                heartbeatIntervalSeconds, 1L, fixture.GroupId, fixture.PolicyId, 1),
            preMutationProjection, null, null, null, null, null, null, null,
            originalHeartbeatAt, heartbeatIntervalSeconds, null, null, null, new[] { controlled.CauseId });
        if (prerequisite == "ProjectionMissing")
        {
            await using var mutation = new EePulseDbContext(fixture.Options);
            mutation.Remove(await mutation.ProbeStatusProjections.SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
            await mutation.SaveChangesAsync(TestContext.Current.CancellationToken);
            evidence = evidence with { ExpectedPreH1Projection = null };
        }
        else if (prerequisite == "AuthorityHeartbeatAdvanced")
        {
            var advancedHeartbeatAt = fixture.Now;
            await SetHeartbeatAsync(fixture, fixture.AgentId, advancedHeartbeatAt);
            evidence = evidence with { AdvancedHeartbeatAt = advancedHeartbeatAt, AdvancedHeartbeatIntervalSeconds = heartbeatIntervalSeconds, ExpectedPreH1Projection = preMutationProjection };
        }
        else if (prerequisite == "VisibleAlreadyUnknown")
        {
            await using var mutation = new EePulseDbContext(fixture.Options);
            await mutation.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET visible_status = {"Unknown"} WHERE probe_id = {fixture.ProbeId}", TestContext.Current.CancellationToken);
            evidence = evidence with { ExpectedPreH1Projection = preMutationProjection with { VisibleStatus = ProbeStatus.Unknown } };
        }
        else
        {
            var advancedHeartbeatAt = (await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken)).AddMinutes(5);
            await SetHeartbeatAsync(fixture, fixture.AgentId, advancedHeartbeatAt);
            var successor = Guid.Parse("50000000-0000-0000-0000-000000000001");
            var at = fixture.Now.AddSeconds(1);
            const ProbeStatus successorOutcome = ProbeStatus.Degraded;
            const int recoveryThreshold = 2;
            await AddLedgerAsync(fixture, at, at, 3, 0m, resultId: successor, averageRtt: 500m);
            var expectedSuccessorProjection = new H1ProjectionSnapshot(
                fixture.ProbeId,
                successorOutcome,
                successorOutcome,
                0,
                Math.Min(preMutationProjection.ConsecutiveSuccessCount + 1, recoveryThreshold),
                preMutationProjection.StateVersion + 1,
                fixture.AgentId,
                successor,
                at,
                at,
                preMutationProjection.OpenIncidentId);
            var successorCreatedBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
            await using var results = new EePulseDbContext(fixture.Options);
            await new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            var successorCreatedAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
            await using var successorEvidence = new EePulseDbContext(fixture.Options);
            var successorCauseId = await successorEvidence.ProbeHeartbeatExpiryCauses.AsNoTracking()
                .Where(x => x.ProbeId == fixture.ProbeId && x.SourceResultId == successor)
                .Select(x => x.CauseId).SingleAsync(TestContext.Current.CancellationToken);
            var successorProjection = await successorEvidence.ProbeStatusProjections.AsNoTracking()
                .Where(x => x.ProbeId == fixture.ProbeId)
                .Select(x => new H1ProjectionSnapshot(x.ProbeId, x.UnderlyingStatus, x.VisibleStatus,
                    x.ConsecutiveFailureCount, x.ConsecutiveSuccessCount, x.StateVersion, x.WatermarkAgentId,
                    x.WatermarkResultId, x.WatermarkEventAt, x.LastFreshEventAt, x.OpenIncidentId))
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(expectedSuccessorProjection, successorProjection);
            evidence = evidence with
            {
                SuccessorResultId = successor,
                SuccessorEventAt = at,
                SuccessorReceiptAt = at,
                SuccessorLastFreshEventAt = at,
                SuccessorCauseId = successorCauseId,
                SuccessorRequestedAtLowerBound = successorCreatedBefore,
                SuccessorRequestedAtUpperBound = successorCreatedAfter,
                AdvancedHeartbeatAt = advancedHeartbeatAt,
                AdvancedHeartbeatIntervalSeconds = heartbeatIntervalSeconds,
                ExpectedPreH1Projection = expectedSuccessorProjection
            };
        }
        var preH1Bound = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        evidence = evidence with { PreH1PostgresBound = preH1Bound };
        await using (var precondition = new EePulseDbContext(fixture.Options))
        {
            var cause = await precondition.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.CauseId == controlled.CauseId, TestContext.Current.CancellationToken);
            var projection = await precondition.ProbeStatusProjections.AsNoTracking().SingleOrDefaultAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            var agent = await precondition.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, TestContext.Current.CancellationToken);
            Assert.Equal((evidence.ControlledCause.CauseId, ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving,
                    fixture.ProbeId, evidence.ControlledCause.AuthorityAgentId, evidence.ControlledCause.SourceResultId, evidence.ControlledCause.SourceCursorEventAt,
                    evidence.ControlledCause.SourceLastHeartbeatReceivedAt, evidence.ControlledCause.SourceHeartbeatIntervalSeconds,
                    evidence.ControlledCause.SourceConfigurationVersion, evidence.ControlledCause.SourceAgentGroupId, evidence.ControlledCause.PolicySnapshotId,
                    evidence.ControlledCause.PolicyVersion, evidence.ControlledCause.SourceLastHeartbeatReceivedAt.AddSeconds(60)),
                (cause.CauseId, cause.CauseType, cause.SourceDisposition, cause.ProbeId, cause.AuthorityAgentId, cause.SourceResultId, cause.SourceCursorEventAt,
                    cause.SourceLastHeartbeatReceivedAt, cause.SourceHeartbeatIntervalSeconds, cause.SourceConfigurationVersion, cause.SourceAgentGroupId,
                    cause.PolicySnapshotId, cause.PolicyVersion, cause.DueAt));
            Assert.Equal(evidence.ExpectedPreH1Projection, projection is null ? null : new H1ProjectionSnapshot(projection.ProbeId,
                projection.UnderlyingStatus, projection.VisibleStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount,
                projection.StateVersion, projection.WatermarkAgentId, projection.WatermarkResultId, projection.WatermarkEventAt,
                projection.LastFreshEventAt, projection.OpenIncidentId));
            Assert.Equal(evidence.AdvancedHeartbeatAt ?? evidence.OriginalHeartbeatAt, agent.LastHeartbeatAt);
            Assert.Equal(evidence.AdvancedHeartbeatIntervalSeconds ?? evidence.OriginalHeartbeatIntervalSeconds, agent.HeartbeatIntervalSeconds);
            if (prerequisite == "AuthorityWatermarkSuperseded")
            {
                Assert.NotNull(projection);
                Assert.Equal((fixture.AgentId, evidence.SuccessorResultId!.Value, evidence.SuccessorEventAt!.Value, evidence.SuccessorLastFreshEventAt!.Value),
                    (projection!.WatermarkAgentId, projection.WatermarkResultId, projection.WatermarkEventAt, projection.LastFreshEventAt));
                var successorCause = await precondition.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.CauseId == evidence.SuccessorCauseId!.Value, TestContext.Current.CancellationToken);
                var successorTransition = await precondition.ProbeResultStatusTransitions.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ResultId == evidence.SuccessorResultId!.Value, TestContext.Current.CancellationToken);
                Assert.Equal((fixture.AgentId, evidence.SuccessorResultId!.Value, evidence.SuccessorEventAt!.Value, evidence.SuccessorReceiptAt!.Value),
                    (successorTransition.AgentId, successorTransition.ResultId, successorTransition.EventAt, successorTransition.ReceivedAt));
                Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId,
                        evidence.SuccessorResultId!.Value, evidence.SuccessorEventAt!.Value, evidence.AdvancedHeartbeatAt!.Value, evidence.AdvancedHeartbeatIntervalSeconds!.Value, 1L,
                        fixture.GroupId, fixture.PolicyId, 1, evidence.AdvancedHeartbeatAt!.Value.AddSeconds(60)),
                    (successorCause.CauseType, successorCause.SourceDisposition, successorCause.ProbeId, successorCause.AuthorityAgentId,
                        successorCause.SourceResultId, successorCause.SourceCursorEventAt, successorCause.SourceLastHeartbeatReceivedAt,
                        successorCause.SourceHeartbeatIntervalSeconds, successorCause.SourceConfigurationVersion, successorCause.SourceAgentGroupId,
                        successorCause.PolicySnapshotId, successorCause.PolicyVersion, successorCause.DueAt));
                Assert.InRange(successorCause.RequestedAt, evidence.SuccessorRequestedAtLowerBound!.Value, evidence.SuccessorRequestedAtUpperBound!.Value);
                Assert.True(successorCause.DueAt > evidence.PreH1PostgresBound!.Value);
            }
            else
                Assert.False(await precondition.ProbeHeartbeatExpiryCauses.AsNoTracking().AnyAsync(x => x.ProbeId == fixture.ProbeId && x.CauseId != evidence.ControlledCause.CauseId, TestContext.Current.CancellationToken));
        }
        var baseline = await CaptureH1NoMutationSnapshotAsync(fixture);
        var before = evidence.PreH1PostgresBound!.Value;
        await using (var eligibility = new EePulseDbContext(fixture.Options))
        {
            var due = await eligibility.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && x.DueAt <= before && !eligibility.ProbeHeartbeatExpiryCauseDispositions.Any(d => d.CauseId == x.CauseId)).Select(x => x.CauseId).ToArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal(evidence.ExpectedDueCauseIds, due);
        }
        ProbeHeartbeatExpiryProcessorOutcome outcome;
        await using (var processor = new EePulseDbContext(fixture.Options)) outcome = await new ProbeHeartbeatExpiryCauseProcessor(processor).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using var verify = new EePulseDbContext(fixture.Options);
        var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == controlled.CauseId, TestContext.Current.CancellationToken);
        Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, controlled.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, reasonCode), (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
        Assert.Equal((controlled.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, reasonCode), (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode));
        Assert.InRange(disposition.ExpiryCutoffReceivedAt, before, after); Assert.Null(disposition.AppliedAt);
        Assert.False(await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().AnyAsync(x => x.CauseId == controlled.CauseId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        var post = await CaptureH1NoMutationSnapshotAsync(fixture);
        AssertH1NoOpDelta(baseline, post, controlled.CauseId, fixture.ProbeId, fixture.PolicyId, reasonCode, disposition.ExpiryCutoffReceivedAt);
    }

    [Fact]
    public async Task St10bH1DrainedResultCreatesAndAppliesItsHeartbeatCauseInTheSameInvocation()
    {
        await using var fixture = await CreateFixtureAsync();
        var resultId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var oldHeartbeat = (await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken)).AddMinutes(-2);
        var eventAt = fixture.Now;
        await SetHeartbeatAsync(fixture, fixture.AgentId, oldHeartbeat);
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: resultId);

        var preArtifacts = await ReadProbeArtifactsAsync(fixture);
        var pre = await CaptureH1NoMutationSnapshotAsync(fixture);
        Assert.Equal(new[] { new LedgerOrder(fixture.AgentId, resultId, eventAt, eventAt) }, await ReadPendingOrderAsync(fixture));
        Assert.Null(pre.Projection); Assert.Empty(pre.ResultDispositions); Assert.Empty(pre.ResultTransitions); Assert.Empty(pre.FreshnessCauses); Assert.Empty(pre.HeartbeatCauses); Assert.Empty(pre.Dispositions); Assert.Empty(pre.Transitions);
        Assert.Empty(preArtifacts.Incidents); Assert.Empty(preArtifacts.Events); Assert.Empty(preArtifacts.Contexts);
        var ledgerBefore = await ReadLedgerAsync(fixture, resultId);
        await using (var precondition = new EePulseDbContext(fixture.Options))
        {
            var agent = await precondition.Agents.AsNoTracking().SingleAsync(x => x.Id == fixture.AgentId, TestContext.Current.CancellationToken);
            var acknowledgement = await precondition.AgentConfigurationAcknowledgements.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ConfigurationVersion == 1, TestContext.Current.CancellationToken);
            var boundary = await precondition.AgentConfigurationEffectiveBoundaries.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ConfigurationVersion == 1, TestContext.Current.CancellationToken);
            var configuration = await precondition.AgentConfigurationSnapshots.AsNoTracking().SingleAsync(x => x.AgentGroupId == fixture.GroupId && x.Version == 1, TestContext.Current.CancellationToken);
            var binding = await precondition.ProbeStatusPolicyBindings.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId && x.ConfigurationVersion == 1, TestContext.Current.CancellationToken);
            var policy = await precondition.ProbeStatusPolicySnapshots.AsNoTracking().SingleAsync(x => x.Id == fixture.PolicyId, TestContext.Current.CancellationToken);
            Assert.Equal((fixture.AgentId, fixture.GroupId, oldHeartbeat, 20, 0L, 0L), (agent.Id, agent.AgentGroupId, agent.LastHeartbeatAt, agent.HeartbeatIntervalSeconds, agent.DesiredConfigurationVersion, agent.LastAppliedConfigurationVersion));
            Assert.Equal((fixture.AgentId, 1L, AgentAcknowledgementStatus.Applied, fixture.Now, fixture.Now, fixture.Now, 1L, 1L), (acknowledgement.AgentId, acknowledgement.ConfigurationVersion, acknowledgement.Status, acknowledgement.AppliedAt, acknowledgement.SentAt, acknowledgement.ReceivedAt, acknowledgement.CentralEffectiveConfigurationVersion, acknowledgement.DesiredConfigurationVersion));
            Assert.Equal((fixture.AgentId, 1L, acknowledgement.Id, AgentAcknowledgementStatus.Applied, fixture.Now), (boundary.AgentId, boundary.ConfigurationVersion, boundary.SourceAcknowledgementId, boundary.SourceAcknowledgementStatus, boundary.AppliedAcknowledgementReceivedAt));
            Assert.Equal((fixture.GroupId, 1L, fixture.Now, (long?)null), (configuration.AgentGroupId, configuration.Version, configuration.GeneratedAt, configuration.RollbackOfVersion));
            Assert.Equal((fixture.ProbeId, 1L, fixture.GroupId, fixture.PolicyId), (binding.ProbeId, binding.ConfigurationVersion, binding.AgentGroupId, binding.PolicySnapshotId));
            Assert.Equal((fixture.PolicyId, 1), (policy.Id, policy.PolicyVersion));
            Assert.True(ledgerBefore.ReceivedAt >= boundary.AppliedAcknowledgementReceivedAt);
            Assert.Equal((1L, fixture.GroupId, fixture.PolicyId, 1), (ledgerBefore.ConfigurationVersion, configuration.AgentGroupId, binding.PolicySnapshotId, policy.PolicyVersion));
        }
        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        Assert.True(ledgerBefore.ReceivedAt <= before); Assert.True(oldHeartbeat.AddSeconds(60) <= before);

        ProbeHeartbeatExpiryProcessorOutcome outcome;
        await using (var processor = new EePulseDbContext(fixture.Options))
            outcome = await new ProbeHeartbeatExpiryCauseProcessor(processor).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var resultDisposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ResultId == resultId, TestContext.Current.CancellationToken);
        var resultTransition = await verify.ProbeResultStatusTransitions.AsNoTracking().SingleAsync(x => x.AgentId == fixture.AgentId && x.ResultId == resultId, TestContext.Current.CancellationToken);
        var heartbeatCause = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId && x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == resultId, TestContext.Current.CancellationToken);
        var freshnessCauses = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).ToArrayAsync(TestContext.Current.CancellationToken);
        var freshnessCause = Assert.Single(freshnessCauses);
        var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == heartbeatCause.CauseId, TestContext.Current.CancellationToken);
        var transition = await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().SingleAsync(x => x.CauseId == heartbeatCause.CauseId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        var artifacts = await ReadProbeArtifactsAsync(fixture);

        Assert.NotEqual(Guid.Empty, heartbeatCause.CauseId);
        Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.Applied, heartbeatCause.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode), (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
        Assert.Equal((fixture.AgentId, resultId, fixture.ProbeId, eventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1), (resultDisposition.AgentId, resultDisposition.ResultId, resultDisposition.ProbeId, resultDisposition.EventAt, resultDisposition.Disposition, resultDisposition.ReasonCode, resultDisposition.ResolvedPolicySnapshotId, resultDisposition.ResolvedPolicyVersion));
        Assert.Equal((fixture.AgentId, resultId, fixture.ProbeId, ProbeStatus.Unknown, ProbeStatus.Up, "bootstrap-success", eventAt, eventAt, ProbeResultProcessingDispositionKind.StateDriving), (resultTransition.AgentId, resultTransition.ResultId, resultTransition.ProbeId, resultTransition.FromStatus, resultTransition.ToStatus, resultTransition.ReasonCode, resultTransition.EventAt, resultTransition.ReceivedAt, resultTransition.ProcessingDisposition));
        Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, resultId, eventAt, oldHeartbeat, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, oldHeartbeat.AddSeconds(60)), (heartbeatCause.CauseType, heartbeatCause.SourceDisposition, heartbeatCause.ProbeId, heartbeatCause.AuthorityAgentId, heartbeatCause.SourceResultId, heartbeatCause.SourceCursorEventAt, heartbeatCause.SourceLastHeartbeatReceivedAt, heartbeatCause.SourceHeartbeatIntervalSeconds, heartbeatCause.SourceConfigurationVersion, heartbeatCause.SourceAgentGroupId, heartbeatCause.PolicySnapshotId, heartbeatCause.PolicyVersion, heartbeatCause.DueAt));
        Assert.InRange(heartbeatCause.RequestedAt, before, after);
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId && x.AuthorityAgentId == fixture.AgentId && x.SourceResultId == resultId && x.SourceCursorEventAt == eventAt && x.SourceLastHeartbeatReceivedAt == oldHeartbeat && x.SourceHeartbeatIntervalSeconds == 20, TestContext.Current.CancellationToken));
        Assert.NotEqual(Guid.Empty, freshnessCause.CauseId); Assert.NotEqual(heartbeatCause.CauseId, freshnessCause.CauseId);
        Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, resultId, eventAt, eventAt, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60, eventAt.AddSeconds(60)), (freshnessCause.CauseType, freshnessCause.SourceDisposition, freshnessCause.ProbeId, freshnessCause.SourceAgentId, freshnessCause.SourceResultId, freshnessCause.SourceCursorEventAt, freshnessCause.SourceLastFreshEventAt, freshnessCause.SourceConfigurationVersion, freshnessCause.SourceAgentGroupId, freshnessCause.PolicySnapshotId, freshnessCause.PolicyVersion, freshnessCause.FreshnessIntervalSeconds, freshnessCause.FreshnessGraceSeconds, freshnessCause.DueAt));
        Assert.InRange(freshnessCause.RequestedAt, before, after);
        Assert.Equal(1, await verify.ProbeFreshnessExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId && x.SourceAgentId == fixture.AgentId && x.SourceResultId == resultId && x.SourceCursorEventAt == eventAt && x.SourceLastFreshEventAt == eventAt, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == freshnessCause.CauseId, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().AnyAsync(x => x.CauseId == freshnessCause.CauseId, TestContext.Current.CancellationToken));
        Assert.Equal((heartbeatCause.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode), (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode));
        Assert.Equal((heartbeatCause.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeStatus.Up, ProbeStatus.Unknown, ProbeHeartbeatExpiryCauseTransition.AgentHeartbeatExpiredReasonCode), (transition.CauseId, transition.ProbeId, transition.PolicySnapshotId, transition.PolicyVersion, transition.DispositionOutcome, transition.FromVisibleStatus, transition.ToVisibleStatus, transition.ReasonCode));
        Assert.Equal(disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt); Assert.Equal(disposition.AppliedAt, transition.AppliedAt); Assert.Equal(transition.AppliedAt, resultDisposition.DecidedAt); Assert.InRange(disposition.ExpiryCutoffReceivedAt, before, after);
        Assert.Equal((ProbeStatus.Up, ProbeStatus.Unknown, 0, 1, eventAt, eventAt, fixture.AgentId, resultId, 2L, (Guid?)null), (projection.UnderlyingStatus, projection.VisibleStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId, projection.StateVersion, projection.OpenIncidentId));
        Assert.True(preArtifacts.Incidents.SequenceEqual(artifacts.Incidents)); Assert.True(preArtifacts.Events.SequenceEqual(artifacts.Events)); Assert.True(preArtifacts.Contexts.SequenceEqual(artifacts.Contexts));
        Assert.Equal(1, await verify.ProbeResultProcessingDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeResultStatusTransitions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeResultLedgerEntries.AsNoTracking().AnyAsync(x => x.ProbeId == fixture.ProbeId && !verify.ProbeResultProcessingDispositions.Any(d => d.AgentId == x.AgentId && d.ResultId == x.ResultId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10bH1FinalStateVersionFlushFailureRollsBackThenRetryAndReplayAreCoherent()
    {
        await using var fixture = await CreateFixtureAsync();
        var due = await CreateDueAuthoritativeH1CauseAsync(fixture, ProbeStatus.Up);
        var baseline = await CaptureH1NoMutationSnapshotAsync(fixture);
        Assert.NotNull(baseline.Projection);
        Assert.Equal((fixture.AgentId, due.ResultId, due.EventAt, due.EventAt, ProbeStatus.Up, 1L), (baseline.Projection!.WatermarkAgentId, baseline.Projection.WatermarkResultId, baseline.Projection.WatermarkEventAt, baseline.Projection.LastFreshEventAt, baseline.Projection.VisibleStatus, baseline.Projection.StateVersion));
        var controlledCause = Assert.Single(baseline.HeartbeatCauses);
        Assert.Equal((due.CauseId, ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, due.ResultId, due.EventAt, fixture.Now.AddMinutes(-2), 20, 1L, fixture.GroupId, fixture.PolicyId, 1, fixture.Now.AddMinutes(-1)), (controlledCause.CauseId, controlledCause.CauseType, controlledCause.SourceDisposition, controlledCause.ProbeId, controlledCause.AuthorityAgentId, controlledCause.SourceResultId, controlledCause.SourceCursorEventAt, controlledCause.SourceLastHeartbeatReceivedAt, controlledCause.SourceHeartbeatIntervalSeconds, controlledCause.SourceConfigurationVersion, controlledCause.SourceAgentGroupId, controlledCause.PolicySnapshotId, controlledCause.PolicyVersion, controlledCause.DueAt));
        Assert.Empty(baseline.Dispositions); Assert.Empty(baseline.Transitions); Assert.True(controlledCause.DueAt <= await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken));

        var interceptor = new ThrowOnHeartbeatStateVersionUpdateInterceptor();
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(interceptor).Options;
        await using (var failing = new EePulseDbContext(failingOptions))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeHeartbeatExpiryCauseProcessor(failing).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.Equal("st10b-h1-final-state-version-flush-failure", failure.Message);
        }
        Assert.True(interceptor.WasTriggered); Assert.Equal(1, interceptor.TriggerCount);
        var rolledBack = await CaptureH1NoMutationSnapshotAsync(fixture);
        AssertH1NoMutationSnapshotEqual(baseline, rolledBack);
        Assert.Equal((ProbeStatus.Up, 1L), (rolledBack.Projection!.VisibleStatus, rolledBack.Projection.StateVersion)); Assert.Empty(rolledBack.Dispositions); Assert.Empty(rolledBack.Transitions);
        Assert.Equal(controlledCause, Assert.Single(rolledBack.HeartbeatCauses));

        var retryBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        ProbeHeartbeatExpiryProcessorOutcome retry;
        await using (var normal = new EePulseDbContext(fixture.Options)) retry = await new ProbeHeartbeatExpiryCauseProcessor(normal).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        var retryAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        var successful = await CaptureH1NoMutationSnapshotAsync(fixture);
        await using (var verify = new EePulseDbContext(fixture.Options))
        {
            var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == due.CauseId, TestContext.Current.CancellationToken);
            var transition = await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().SingleAsync(x => x.CauseId == due.CauseId, TestContext.Current.CancellationToken);
            Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.Applied, due.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode), (retry.Kind, retry.CauseId, retry.DispositionOutcome, retry.ReasonCode));
            Assert.Equal((due.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode), (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode));
            Assert.Equal((due.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeStatus.Up, ProbeStatus.Unknown, ProbeHeartbeatExpiryCauseTransition.AgentHeartbeatExpiredReasonCode), (transition.CauseId, transition.ProbeId, transition.PolicySnapshotId, transition.PolicyVersion, transition.DispositionOutcome, transition.FromVisibleStatus, transition.ToVisibleStatus, transition.ReasonCode));
            Assert.Equal(disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt); Assert.Equal(disposition.AppliedAt, transition.AppliedAt); Assert.InRange(disposition.ExpiryCutoffReceivedAt, retryBefore, retryAfter);
            Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        }
        Assert.Equal(baseline.Projection! with { VisibleStatus = ProbeStatus.Unknown, StateVersion = baseline.Projection.StateVersion + 1 }, successful.Projection);
        Assert.True(baseline.Ledger.SequenceEqual(successful.Ledger)); Assert.True(baseline.ResultDispositions.SequenceEqual(successful.ResultDispositions)); Assert.True(baseline.ResultTransitions.SequenceEqual(successful.ResultTransitions)); Assert.True(baseline.FreshnessCauses.SequenceEqual(successful.FreshnessCauses)); Assert.True(baseline.HeartbeatCauses.SequenceEqual(successful.HeartbeatCauses)); Assert.True(baseline.Artifacts.Incidents.SequenceEqual(successful.Artifacts.Incidents)); Assert.True(baseline.Artifacts.Events.SequenceEqual(successful.Artifacts.Events)); Assert.True(baseline.Artifacts.Contexts.SequenceEqual(successful.Artifacts.Contexts));
        Assert.Equal(new[] { new HeartbeatDispositionFullSnapshot(due.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeHeartbeatExpiryCauseDisposition.AgentHeartbeatExpiredReasonCode, successful.Dispositions.Single().ExpiryCutoffReceivedAt, successful.Dispositions.Single().AppliedAt) }, successful.Dispositions);
        Assert.Single(successful.Transitions);

        await using (var replay = new EePulseDbContext(fixture.Options))
            Assert.Equal(new ProbeHeartbeatExpiryProcessorOutcome(ProbeHeartbeatExpiryProcessorOutcomeKind.NoDueCause), await new ProbeHeartbeatExpiryCauseProcessor(replay).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        var replayed = await CaptureH1NoMutationSnapshotAsync(fixture);
        AssertH1NoMutationSnapshotEqual(successful, replayed);
    }

    [Fact]
    public async Task St10bT4B4H1RetriesWhenRequiredAgentSetChangesBeforeProbeStabilization()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateFixtureAsync();
        var otherAgent = await AddSecondAgentAsync(fixture, fixture.Now);
        var (agentLow, agentHigh) = string.CompareOrdinal(fixture.AgentId.ToString("D"), otherAgent.ToString("D")) < 0 ? (fixture.AgentId, otherAgent) : (otherAgent, fixture.AgentId);
        Assert.True(string.CompareOrdinal(agentLow.ToString("D"), agentHigh.ToString("D")) < 0);
        var resultLow = Guid.Parse("b4000000-0000-0000-0000-000000000001"); var resultHigh = Guid.Parse("b4000000-0000-0000-0000-000000000002");
        var heartbeatLow = (await ReadPostgresTimestampAsync(fixture.ConnectionString, ct)).AddMinutes(-4); var heartbeatHigh = heartbeatLow.AddTicks(10);
        var eventLow = heartbeatLow.AddSeconds(10); var eventHigh = eventLow.AddSeconds(1);
        await SetHeartbeatAsync(fixture, agentLow, heartbeatLow); await SetHeartbeatAsync(fixture, agentHigh, heartbeatHigh);
        var agentHighEvidence = await CaptureT4B4AgentEvidenceAsync(fixture, agentHigh);
        await AddLedgerAsync(fixture, eventLow, eventLow, 3, 0m, resultId: resultLow, agentId: agentLow);
        var oldCauseBefore = await ReadPostgresTimestampAsync(fixture.ConnectionString, ct);
        await using (var seed = new EePulseDbContext(fixture.Options)) Assert.Equal(resultLow, (await new ProbeResultStatusProcessor(seed, new FixedClock(eventLow)).ProcessNextAsync(fixture.ProbeId, ct)).ResultId);
        var oldCauseAfter = await ReadPostgresTimestampAsync(fixture.ConnectionString, ct);
        var baseline = await CaptureH1NoMutationSnapshotAsync(fixture);
        var oldCause = Assert.Single(baseline.HeartbeatCauses);
        Assert.Equal((fixture.ProbeId, ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, agentLow, resultLow, eventLow, heartbeatLow, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, heartbeatLow.AddSeconds(60)), (oldCause.ProbeId, oldCause.CauseType, oldCause.SourceDisposition, oldCause.AuthorityAgentId, oldCause.SourceResultId, oldCause.SourceCursorEventAt, oldCause.SourceLastHeartbeatReceivedAt, oldCause.SourceHeartbeatIntervalSeconds, oldCause.SourceConfigurationVersion, oldCause.SourceAgentGroupId, oldCause.PolicySnapshotId, oldCause.PolicyVersion, oldCause.DueAt));
        Assert.InRange(oldCause.RequestedAt, oldCauseBefore, oldCauseAfter); Assert.True(oldCause.DueAt <= await ReadPostgresTimestampAsync(fixture.ConnectionString, ct)); Assert.Empty(await ReadPendingOrderAsync(fixture)); Assert.Empty(baseline.Dispositions); Assert.Empty(baseline.Transitions); Assert.Single(baseline.ResultDispositions); Assert.Single(baseline.ResultTransitions); Assert.Empty(baseline.Artifacts.Incidents); Assert.Empty(baseline.Artifacts.Events); Assert.Empty(baseline.Artifacts.Contexts);

        var app = $"t4b4-h1-{Guid.NewGuid():N}"; var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = app }.ConnectionString).Options;
        await using var blockerA = new NpgsqlConnection(fixture.ConnectionString); await blockerA.OpenAsync(ct); await using var txA = await blockerA.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct); var pidA = await LockProbeAdvisoryTransactionAsync(blockerA, txA, fixture.ProbeId, ct);
        await using var observer = new NpgsqlConnection(fixture.ConnectionString); await observer.OpenAsync(ct);
        var h1Db = new EePulseDbContext(options); var h1Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); Task<ProbeHeartbeatExpiryProcessorOutcome>? h1Task = null; ProbeHeartbeatExpiryProcessorOutcome? outcome = null; T4B3BackendIdentity? backendB = null; Exception? primary = null; var releasedA = false; var releasedD = false;
        NpgsqlConnection? blockerD = null; NpgsqlTransaction? txD = null;
        NpgsqlConnection? waiterE = null; NpgsqlTransaction? txE = null; NpgsqlCommand? waitECommand = null; CancellationTokenSource? waitECancellation = null; Task<object?>? waitETask = null; T4B3BackendIdentity? backendE = null;
        try
        {
            var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, ct);
            h1Task = RunHeartbeatAsync(h1Db, fixture.ProbeId, value => outcome = value, h1Cancellation.Token);
            backendB = await CaptureT4B3BackendIdentityAsync(observer, app, h1Task, ct); Assert.NotEqual(pidA, backendB.Pid);
            await WaitForH1ProbeBlockedByAsync(observer, backendB, pidA, fixture.ProbeId, h1Task, ct);
            var eApp = $"t4b4-low-waiter-{Guid.NewGuid():N}"; waiterE = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = eApp }.ConnectionString); await waiterE.OpenAsync(ct); txE = await waiterE.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct); var pidE = await GetBackendPidAsync(waiterE, ct); backendE = await CaptureT4B3IdleBackendIdentityAsync(observer, eApp, pidE, ct); Assert.Equal(pidE, backendE.Pid); Assert.NotEqual(backendB.Pid, backendE.Pid); waitECancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); waitECommand = new NpgsqlCommand("SELECT id FROM agents WHERE id=@agentId FOR UPDATE", waiterE, txE); waitECommand.Parameters.AddWithValue("agentId", agentLow); waitETask = waitECommand.ExecuteScalarAsync(waitECancellation.Token).AsTask();
            await WaitForT4B4WaiterEAsync(observer, backendE, backendB, pidA, fixture.ProbeId, waitETask, h1Task, ct);
            await WaitForH1ProbeBlockedByAsync(observer, backendB, pidA, fixture.ProbeId, h1Task, ct);
            await using (var blocking = new NpgsqlCommand("SELECT array_to_string(pg_blocking_pids(@pid), ',')", observer)) { blocking.Parameters.AddWithValue("pid", backendE.Pid); Assert.Equal(backendB.Pid.ToString(), Assert.IsType<string>(await blocking.ExecuteScalarAsync(ct))); }
            var eFailures = new List<Exception>(); var eTerminal = await SettleT4B3TaskAsync("T4B4 agentLow waiter E", waitETask, waitECancellation, backendE, observer, eFailures); Assert.True(eTerminal); Assert.Empty(eFailures); await txE.RollbackAsync(ct); await txE.DisposeAsync(); txE = null; await waitECommand.DisposeAsync(); waitECommand = null; waitECancellation.Dispose(); waitECancellation = null;
            AssertH1NoMutationSnapshotEqual(baseline, await CaptureH1NoMutationSnapshotAsync(fixture));

            await AddLedgerAsync(fixture, eventHigh, eventHigh, 3, 0m, resultId: resultHigh, agentId: agentHigh);
            await using (var changed = new EePulseDbContext(fixture.Options))
            {
                var highAgent = await changed.Agents.AsNoTracking().SingleAsync(x => x.Id == agentHigh, ct); var acknowledgement = await changed.AgentConfigurationAcknowledgements.AsNoTracking().SingleAsync(x => x.AgentId == agentHigh && x.ConfigurationVersion == 1, ct);
                var configuration = await changed.AgentConfigurationSnapshots.AsNoTracking().SingleAsync(x => x.AgentGroupId == fixture.GroupId && x.Version == 1, ct); var policy = await changed.ProbeStatusPolicySnapshots.AsNoTracking().SingleAsync(x => x.Id == fixture.PolicyId && x.PolicyVersion == 1, ct);
                var highLedger = await changed.ProbeResultLedgerEntries.AsNoTracking().SingleAsync(x => x.AgentId == agentHigh && x.ResultId == resultHigh, ct);
                var boundary = await changed.AgentConfigurationEffectiveBoundaries.AsNoTracking().SingleAsync(x => x.AgentId == agentHigh && x.ConfigurationVersion == 1, ct);
                var binding = await changed.ProbeStatusPolicyBindings.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId && x.ConfigurationVersion == 1, ct);
                Assert.Equal((agentHigh, resultHigh, fixture.ProbeId, 1L, eventHigh.AddSeconds(-1), eventHigh, 3, 3, 0m, 1m, 1m, 1m, (string?)null, Convert.ToHexString(new byte[32]), eventHigh), (highLedger.AgentId, highLedger.ResultId, highLedger.ProbeId, highLedger.ConfigurationVersion, highLedger.StartedAt, highLedger.EndedAt, highLedger.AttemptCount, highLedger.SuccessfulAttemptCount, highLedger.PacketLossRatio, highLedger.MinRttMilliseconds, highLedger.AverageRttMilliseconds, highLedger.MaxRttMilliseconds, highLedger.ErrorCategory, Convert.ToHexString(highLedger.ImmutablePayloadDigest), highLedger.ReceivedAt));
                Assert.Equal(agentHighEvidence, new T4B4AgentEvidence(highAgent.Id, highAgent.ClientInstanceId, highAgent.Name, highAgent.MachineName, highAgent.AgentVersion, highAgent.AgentGroupId, highAgent.SelfHealth, highAgent.Status, highAgent.QueueDepth, highAgent.LastHeartbeatAt, highAgent.LastReportedAt, highAgent.HeartbeatIntervalSeconds, highAgent.DesiredConfigurationVersion, highAgent.LastAppliedConfigurationVersion, highAgent.LastConfigurationAcknowledgedAt, highAgent.ClockSkewSuspected, highAgent.CredentialExpiresAt, highAgent.CreatedAt, highAgent.RevokedAt, highAgent.RevocationReason, highAgent.RowVersion));
                Assert.Equal((agentHigh, 1L, AgentAcknowledgementStatus.Applied, fixture.Now, fixture.Now, fixture.Now, (string?)null, 1L, 1L), (acknowledgement.AgentId, acknowledgement.ConfigurationVersion, acknowledgement.Status, acknowledgement.AppliedAt, acknowledgement.SentAt, acknowledgement.ReceivedAt, acknowledgement.ErrorCode, acknowledgement.CentralEffectiveConfigurationVersion, acknowledgement.DesiredConfigurationVersion));
                Assert.Equal((agentHigh, 1L, acknowledgement.Id, AgentAcknowledgementStatus.Applied, fixture.Now), (boundary.AgentId, boundary.ConfigurationVersion, boundary.SourceAcknowledgementId, boundary.SourceAcknowledgementStatus, boundary.AppliedAcknowledgementReceivedAt));
                Assert.Equal((fixture.GroupId, 1L, FreshnessPayload(fixture.ProbeId, 30), Convert.ToHexString(new byte[32]), fixture.Now, (long?)null), (configuration.AgentGroupId, configuration.Version, configuration.Payload, Convert.ToHexString(configuration.PayloadDigest), configuration.GeneratedAt, configuration.RollbackOfVersion));
                Assert.Equal((fixture.PolicyId, 1, 2, 2, (int?)500, (decimal?)null, 300, 60, fixture.Now), (policy.Id, policy.PolicyVersion, policy.FailureThreshold, policy.RecoveryThreshold, policy.WarningRttMilliseconds, policy.WarningPacketLossRatio, policy.ApprovedLatenessSeconds, policy.ApprovedFutureSkewSeconds, policy.CreatedAt));
                Assert.True(highLedger.ReceivedAt >= boundary.AppliedAcknowledgementReceivedAt); Assert.Equal((fixture.ProbeId, 1L, fixture.GroupId, fixture.PolicyId), (binding.ProbeId, binding.ConfigurationVersion, binding.AgentGroupId, binding.PolicySnapshotId));
                Assert.False(await changed.ProbeResultProcessingDispositions.AsNoTracking().AnyAsync(x => x.AgentId == agentHigh && x.ResultId == resultHigh, ct)); Assert.False(await changed.ProbeFreshnessExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceAgentId == agentHigh && x.SourceResultId == resultHigh, ct)); Assert.False(await changed.ProbeHeartbeatExpiryCauses.AsNoTracking().AnyAsync(x => x.AuthorityAgentId == agentHigh && x.SourceResultId == resultHigh, ct));
                var required = (await changed.ProbeResultLedgerEntries.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !changed.ProbeResultProcessingDispositions.Any(d => d.AgentId == x.AgentId && d.ResultId == x.ResultId)).Select(x => x.AgentId).Concat(changed.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !changed.ProbeHeartbeatExpiryCauseDispositions.Any(d => d.CauseId == x.CauseId)).Select(x => x.AuthorityAgentId)).Distinct().ToArrayAsync(ct)).Select(x => x.ToString("D")).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Assert.True(new[] { agentLow.ToString("D"), agentHigh.ToString("D") }.SequenceEqual(required));
            }
            var afterHighCommit = await CaptureH1NoMutationSnapshotAsync(fixture); Assert.Equal(1, afterHighCommit.Ledger.Length - baseline.Ledger.Length); Assert.True(baseline.ResultDispositions.SequenceEqual(afterHighCommit.ResultDispositions)); Assert.True(baseline.HeartbeatCauses.SequenceEqual(afterHighCommit.HeartbeatCauses));
            blockerD = new NpgsqlConnection(fixture.ConnectionString); await blockerD.OpenAsync(ct); txD = await blockerD.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct); var pidD = await LockAgentForUpdateAsync(blockerD, txD, agentHigh, ct); Assert.NotEqual(pidA, pidD); Assert.NotEqual(pidD, backendB.Pid);
            await txA.RollbackAsync(ct); releasedA = true;
            await WaitForTransactionLockEvidenceAsync(observer, backendB.Pid, pidD, requireNoAdvisoryLock: true, h1Task, ct);
            AssertH1NoMutationSnapshotEqual(afterHighCommit, await CaptureH1NoMutationSnapshotAsync(fixture));
            await txD.RollbackAsync(ct); releasedD = true;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(10)); await h1Task.WaitAsync(bounded.Token);
            var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, ct);
            await using var verify = new EePulseDbContext(fixture.Options);
            var highDisposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(x => x.AgentId == agentHigh && x.ResultId == resultHigh, ct); var fresh = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceAgentId == agentHigh && x.SourceResultId == resultHigh, ct); var successor = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.AuthorityAgentId == agentHigh && x.SourceResultId == resultHigh, ct); var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == oldCause.CauseId, ct); var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId, ct); var agentHighAfter = await verify.Agents.AsNoTracking().SingleAsync(x => x.Id == agentHigh, ct);
            Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, oldCause.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode), (outcome!.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
            Assert.Equal(agentHighEvidence, new T4B4AgentEvidence(agentHighAfter.Id, agentHighAfter.ClientInstanceId, agentHighAfter.Name, agentHighAfter.MachineName, agentHighAfter.AgentVersion, agentHighAfter.AgentGroupId, agentHighAfter.SelfHealth, agentHighAfter.Status, agentHighAfter.QueueDepth, agentHighAfter.LastHeartbeatAt, agentHighAfter.LastReportedAt, agentHighAfter.HeartbeatIntervalSeconds, agentHighAfter.DesiredConfigurationVersion, agentHighAfter.LastAppliedConfigurationVersion, agentHighAfter.LastConfigurationAcknowledgedAt, agentHighAfter.ClockSkewSuspected, agentHighAfter.CredentialExpiresAt, agentHighAfter.CreatedAt, agentHighAfter.RevokedAt, agentHighAfter.RevocationReason, agentHighAfter.RowVersion));
            Assert.Equal((agentHigh, resultHigh, fixture.ProbeId, eventHigh, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1), (highDisposition.AgentId, highDisposition.ResultId, highDisposition.ProbeId, highDisposition.EventAt, highDisposition.Disposition, highDisposition.ReasonCode, highDisposition.ResolvedPolicySnapshotId, highDisposition.ResolvedPolicyVersion)); Assert.InRange(highDisposition.DecidedAt, before, after); Assert.False(await verify.ProbeResultStatusTransitions.AsNoTracking().AnyAsync(x => x.AgentId == agentHigh && x.ResultId == resultHigh, ct));
            Assert.Equal((agentHigh, resultHigh, eventHigh, eventHigh, ProbeStatus.Up, ProbeStatus.Up, 0, 2, 2L, (Guid?)null), (projection.WatermarkAgentId, projection.WatermarkResultId, projection.WatermarkEventAt, projection.LastFreshEventAt, projection.UnderlyingStatus, projection.VisibleStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.StateVersion, projection.OpenIncidentId));
            Assert.NotEqual(Guid.Empty, fresh.CauseId); Assert.NotEqual(Guid.Empty, successor.CauseId); Assert.NotEqual(fresh.CauseId, successor.CauseId); Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentHigh, resultHigh, eventHigh, eventHigh, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60, eventHigh.AddSeconds(60)), (fresh.CauseType, fresh.SourceDisposition, fresh.ProbeId, fresh.SourceAgentId, fresh.SourceResultId, fresh.SourceCursorEventAt, fresh.SourceLastFreshEventAt, fresh.SourceConfigurationVersion, fresh.SourceAgentGroupId, fresh.PolicySnapshotId, fresh.PolicyVersion, fresh.FreshnessIntervalSeconds, fresh.FreshnessGraceSeconds, fresh.DueAt)); Assert.InRange(fresh.RequestedAt, before, after);
            Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentHigh, resultHigh, eventHigh, heartbeatHigh, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, heartbeatHigh.AddSeconds(60)), (successor.CauseType, successor.SourceDisposition, successor.ProbeId, successor.AuthorityAgentId, successor.SourceResultId, successor.SourceCursorEventAt, successor.SourceLastHeartbeatReceivedAt, successor.SourceHeartbeatIntervalSeconds, successor.SourceConfigurationVersion, successor.SourceAgentGroupId, successor.PolicySnapshotId, successor.PolicyVersion, successor.DueAt)); Assert.InRange(successor.RequestedAt, before, after);
            Assert.Equal((oldCause.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode, (DateTimeOffset?)null), (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode, disposition.AppliedAt)); Assert.InRange(disposition.ExpiryCutoffReceivedAt, before, after); Assert.Equal(highDisposition.DecidedAt, disposition.ExpiryCutoffReceivedAt); Assert.Empty(await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().Where(x => x.CauseId == oldCause.CauseId || x.CauseId == successor.CauseId).ToArrayAsync(ct)); Assert.False(await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == successor.CauseId, ct));
            var final = await CaptureH1NoMutationSnapshotAsync(fixture);
            var expectedFinal = baseline with
            {
                Projection = new St10ProjectionSnapshot(fixture.ProbeId, ProbeStatus.Up, ProbeStatus.Up, 0, 2, eventHigh, eventHigh, agentHigh, resultHigh, 2, null),
                Ledger = baseline.Ledger.Append(new H1LedgerFullSnapshot(agentHigh, resultHigh, fixture.ProbeId, 1, eventHigh.AddSeconds(-1), eventHigh, 3, 3, 0m, 1m, 1m, 1m, null, Convert.ToHexString(new byte[32]), eventHigh)).OrderBy(x => x.AgentId).ThenBy(x => x.ResultId).ToArray(),
                ResultDispositions = baseline.ResultDispositions.Append(new St10ResultDispositionSnapshot(agentHigh, resultHigh, fixture.ProbeId, eventHigh, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1, highDisposition.DecidedAt)).OrderBy(x => x.AgentId).ThenBy(x => x.ResultId).ToArray(),
                FreshnessCauses = baseline.FreshnessCauses.Append(new FreshnessFullSnapshot(fresh.CauseId, fixture.ProbeId, ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, agentHigh, resultHigh, eventHigh, eventHigh, 1, fixture.GroupId, ProbeResultProcessingDispositionKind.StateDriving, fixture.PolicyId, 1, 30, 60, eventHigh.AddSeconds(60), fresh.RequestedAt)).OrderBy(x => x.CauseId).ToArray(),
                HeartbeatCauses = baseline.HeartbeatCauses.Append(new HeartbeatFullSnapshot(successor.CauseId, fixture.ProbeId, ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, agentHigh, resultHigh, eventHigh, heartbeatHigh, 20, 1, fixture.GroupId, ProbeResultProcessingDispositionKind.StateDriving, fixture.PolicyId, 1, heartbeatHigh.AddSeconds(60), successor.RequestedAt)).OrderBy(x => x.CauseId).ToArray(),
                Dispositions = baseline.Dispositions.Append(new HeartbeatDispositionFullSnapshot(oldCause.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode, disposition.ExpiryCutoffReceivedAt, null)).OrderBy(x => x.CauseId).ToArray()
            };
            AssertH1NoMutationSnapshotEqual(expectedFinal, final); Assert.Equal(oldCause, final.HeartbeatCauses.Single(x => x.CauseId == oldCause.CauseId));
        }
        catch (Exception exception) { primary = exception; throw; }
        finally
        {
            var failures = new List<Exception>(); async Task Attempt(string name, Func<CancellationToken, Task> action) { try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await action(cleanup.Token); } catch (Exception exception) { failures.Add(new InvalidOperationException($"T4B4 cleanup failed while {name}.", exception)); } }
            var eTransferred = false;
            if (waitETask is not null && waitECancellation is not null && !await SettleT4B3TaskAsync("T4B4 agentLow waiter E", waitETask, waitECancellation, backendE, observer, failures))
            {
                var eCommandForDeferred = waitECommand; var eTransactionForDeferred = txE; var eConnectionForDeferred = waiterE; var eResources = new List<T4B3DeferredResource>(); if (eCommandForDeferred is not null) eResources.Add(new("agentLow waiter E command", async () => await eCommandForDeferred.DisposeAsync())); if (eTransactionForDeferred is not null) eResources.Add(new("agentLow waiter E transaction", async () => await eTransactionForDeferred.DisposeAsync())); if (eConnectionForDeferred is not null) eResources.Add(new("agentLow waiter E connection", async () => await eConnectionForDeferred.DisposeAsync()));
                TransferT4B3Ownership("T4B4 agentLow waiter E", waitETask, waitECancellation, backendE, eResources, failures, primary); eTransferred = true; waitECommand = null; txE = null; waiterE = null; waitECancellation = null;
            }
            if (!eTransferred) { if (waitECommand is not null) await Attempt("disposing agentLow waiter E command", async _ => await waitECommand.DisposeAsync()); if (txE is not null) await Attempt("releasing agentLow waiter E", async token => await txE.RollbackAsync(token)); if (waitECancellation is not null) await Attempt("disposing agentLow waiter E CTS", _ => { waitECancellation.Dispose(); return Task.CompletedTask; }); if (waiterE is not null) await Attempt("disposing agentLow waiter E connection", async _ => await waiterE.DisposeAsync()); }
            if (!releasedA) await Attempt("releasing Probe blocker A", async token => await txA.RollbackAsync(token)); if (txD is not null && !releasedD) await Attempt("releasing Agent blocker D", async token => await txD.RollbackAsync(token));
            var terminal = h1Task is not null && await SettleT4B3TaskAsync("T4B4 H1 B", h1Task, h1Cancellation, backendB, observer, failures);
            if (h1Task is null || terminal) { await Attempt("disposing H1 context", async _ => await h1Db.DisposeAsync()); await Attempt("disposing H1 CTS", _ => { h1Cancellation.Dispose(); return Task.CompletedTask; }); } else await Attempt("transferring H1 ownership", _ => { TransferT4B3Ownership("T4B4 H1 B", h1Task, h1Cancellation, backendB, [new("T4B4 H1 context", async () => await h1Db.DisposeAsync())], failures, primary); return Task.CompletedTask; });
            if (txD is not null) await Attempt("disposing Agent blocker D transaction", async _ => await txD.DisposeAsync()); if (blockerD is not null) await Attempt("disposing Agent blocker D connection", async _ => await blockerD.DisposeAsync());
            if (failures.Count > 0) { if (primary is not null) for (var i = 0; i < failures.Count; i++) primary.Data[$"T4B4CleanupFailure{i + 1}"] = failures[i]; else if (failures.Count == 1) throw failures[0]; else throw new AggregateException(failures); }
        }
    }

    [Fact]
    public async Task St10bH1RetriesWhenRequiredAgentSetChangesBeforeProbeStabilization()
    {
        await using var fixture = await CreateFixtureAsync();
        var resultA = Guid.Parse("80000000-0000-0000-0000-000000000001");
        var resultB = Guid.Parse("80000000-0000-0000-0000-000000000002");
        var heartbeatA = (await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken)).AddMinutes(-3);
        var heartbeatB = heartbeatA.AddTicks(10);
        var agentB = await AddSecondAgentAsync(fixture, heartbeatB);
        var eventA = fixture.Now;
        var eventB = fixture.Now.AddSeconds(1);
        await SetHeartbeatAsync(fixture, fixture.AgentId, heartbeatA);
        await AddLedgerAsync(fixture, eventA, eventA, 3, 0m, resultId: resultA);
        await using (var processA = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultA, (await new ProbeResultStatusProcessor(processA, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        var baseline = await CaptureH1NoMutationSnapshotAsync(fixture);
        var aCause = Assert.Single(baseline.HeartbeatCauses);
        Assert.Equal((fixture.ProbeId, fixture.AgentId, resultA, eventA, heartbeatA, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, heartbeatA.AddSeconds(60)), (aCause.ProbeId, aCause.AuthorityAgentId, aCause.SourceResultId, aCause.SourceCursorEventAt, aCause.SourceLastHeartbeatReceivedAt, aCause.SourceHeartbeatIntervalSeconds, aCause.SourceConfigurationVersion, aCause.SourceAgentGroupId, aCause.PolicySnapshotId, aCause.PolicyVersion, aCause.DueAt));
        Assert.True(aCause.DueAt <= await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken));
        Assert.Empty(await ReadPendingOrderAsync(fixture)); Assert.Empty(baseline.Dispositions); Assert.Empty(baseline.Transitions);

        var gate = new FirstAgentShareGate();
        var gatedOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(gate).Options;
        var before = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        await using var processorContext = new EePulseDbContext(gatedOptions);
        var invocation = new ProbeHeartbeatExpiryCauseProcessor(processorContext).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        ProbeHeartbeatExpiryProcessorOutcome outcome = default!;
        Exception? primary = null;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); wait.CancelAfter(TimeSpan.FromSeconds(10));
            await gate.WaitUntilEnteredAsync(wait.Token);
            var firstAgentShareCommand = Assert.Single(gate.AgentShareCommands); Assert.True(ImmutableArray.Create(fixture.AgentId.ToString("D")).SequenceEqual(firstAgentShareCommand.AgentIds));
            await AddLedgerAsync(fixture, eventB, eventB, 3, 0m, resultId: resultB, agentId: agentB);
            await using (var whileGated = new EePulseDbContext(fixture.Options))
            {
                Assert.True(await whileGated.ProbeResultLedgerEntries.AsNoTracking().AnyAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken));
                Assert.False(await whileGated.ProbeResultProcessingDispositions.AsNoTracking().AnyAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken));
                Assert.False(await whileGated.ProbeFreshnessExpiryCauses.AsNoTracking().AnyAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken));
                Assert.False(await whileGated.ProbeHeartbeatExpiryCauses.AsNoTracking().AnyAsync(x => x.AuthorityAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken));
                var required = (await whileGated.ProbeResultLedgerEntries.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !whileGated.ProbeResultProcessingDispositions.Any(d => d.AgentId == x.AgentId && d.ResultId == x.ResultId)).Select(x => x.AgentId).Concat(whileGated.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !whileGated.ProbeHeartbeatExpiryCauseDispositions.Any(d => d.CauseId == x.CauseId)).Select(x => x.AuthorityAgentId)).Distinct().ToArrayAsync(TestContext.Current.CancellationToken)).Select(x => x.ToString("D")).ToArray();
                Assert.Equal(new[] { fixture.AgentId.ToString("D"), agentB.ToString("D") }.OrderBy(id => id, StringComparer.Ordinal), required.OrderBy(id => id, StringComparer.Ordinal));
            }
            gate.Release.TrySetResult();
            outcome = await invocation;
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            gate.Release.TrySetResult();
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await invocation.WaitAsync(cleanup.Token);
            }
            catch (Exception cleanupFailure)
            {
                if (primary is not null) primary.Data["T3B3AgentShareGateCleanupFailure"] = cleanupFailure;
                else throw;
            }
        }
        var after = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
        var expectedCanonical = new[] { fixture.AgentId.ToString("D"), agentB.ToString("D") }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var acquisitions = gate.AgentShareCommands;
        var expectedHistory = new[]
        {
            new AgentShareCommand(ImmutableArray.Create(fixture.AgentId.ToString("D"))),
            new AgentShareCommand(ImmutableArray.Create(expectedCanonical[0])),
            new AgentShareCommand(ImmutableArray.Create(expectedCanonical[1]))
        };
        Assert.Equal(1, gate.TriggerCount); Assert.Equal(expectedHistory.Length, acquisitions.Length); for (var index = 0; index < expectedHistory.Length; index++) Assert.True(expectedHistory[index].AgentIds.SequenceEqual(acquisitions[index].AgentIds)); Assert.True(expectedCanonical.SequenceEqual(acquisitions.Skip(1).SelectMany(command => command.AgentIds))); Assert.All(acquisitions, command => Assert.All(command.AgentIds, id => Assert.Equal(id.ToLowerInvariant(), id)));

        await using var verify = new EePulseDbContext(fixture.Options);
        var bDisposition = await verify.ProbeResultProcessingDispositions.AsNoTracking().SingleAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken);
        var bFreshness = await verify.ProbeFreshnessExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken);
        var bHeartbeat = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.AuthorityAgentId == agentB && x.SourceResultId == resultB, TestContext.Current.CancellationToken);
        var aDisposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == aCause.CauseId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, aCause.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode), (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
        Assert.Equal((agentB, resultB, fixture.ProbeId, eventB, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", fixture.PolicyId, 1), (bDisposition.AgentId, bDisposition.ResultId, bDisposition.ProbeId, bDisposition.EventAt, bDisposition.Disposition, bDisposition.ReasonCode, bDisposition.ResolvedPolicySnapshotId, bDisposition.ResolvedPolicyVersion));
        Assert.False(await verify.ProbeResultStatusTransitions.AsNoTracking().AnyAsync(x => x.AgentId == agentB && x.ResultId == resultB, TestContext.Current.CancellationToken));
        Assert.Equal((ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentB, resultB, eventB, eventB, 1L, fixture.GroupId, fixture.PolicyId, 1, 30, 60, eventB.AddSeconds(60)), (bFreshness.CauseType, bFreshness.SourceDisposition, bFreshness.ProbeId, bFreshness.SourceAgentId, bFreshness.SourceResultId, bFreshness.SourceCursorEventAt, bFreshness.SourceLastFreshEventAt, bFreshness.SourceConfigurationVersion, bFreshness.SourceAgentGroupId, bFreshness.PolicySnapshotId, bFreshness.PolicyVersion, bFreshness.FreshnessIntervalSeconds, bFreshness.FreshnessGraceSeconds, bFreshness.DueAt));
        Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, agentB, resultB, eventB, heartbeatB, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, heartbeatB.AddSeconds(60)), (bHeartbeat.CauseType, bHeartbeat.SourceDisposition, bHeartbeat.ProbeId, bHeartbeat.AuthorityAgentId, bHeartbeat.SourceResultId, bHeartbeat.SourceCursorEventAt, bHeartbeat.SourceLastHeartbeatReceivedAt, bHeartbeat.SourceHeartbeatIntervalSeconds, bHeartbeat.SourceConfigurationVersion, bHeartbeat.SourceAgentGroupId, bHeartbeat.PolicySnapshotId, bHeartbeat.PolicyVersion, bHeartbeat.DueAt));
        Assert.InRange(bFreshness.RequestedAt, before, after); Assert.InRange(bHeartbeat.RequestedAt, before, after); Assert.Equal(bDisposition.DecidedAt, aDisposition.ExpiryCutoffReceivedAt); Assert.InRange(aDisposition.ExpiryCutoffReceivedAt, before, after); Assert.True(aCause.DueAt < bHeartbeat.DueAt);
        Assert.Equal((agentB, resultB, eventB, eventB, ProbeStatus.Up, ProbeStatus.Up, 0, 2, 2L, (Guid?)null), (projection.WatermarkAgentId, projection.WatermarkResultId, projection.WatermarkEventAt, projection.LastFreshEventAt, projection.UnderlyingStatus, projection.VisibleStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.StateVersion, projection.OpenIncidentId));
        Assert.Equal((aCause.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode, (DateTimeOffset?)null), (aDisposition.CauseId, aDisposition.ProbeId, aDisposition.PolicySnapshotId, aDisposition.PolicyVersion, aDisposition.Outcome, aDisposition.ReasonCode, aDisposition.AppliedAt));
        Assert.False(await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().AnyAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
        Assert.False(await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == bHeartbeat.CauseId, TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.ProbeResultProcessingDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)); Assert.Equal(1, await verify.ProbeResultStatusTransitions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)); Assert.Equal(2, await verify.ProbeFreshnessExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)); Assert.Equal(2, await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)); Assert.Equal(1, await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().CountAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)); Assert.Empty(await verify.AvailabilityIncidents.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).ToArrayAsync(TestContext.Current.CancellationToken)); Assert.Empty(await verify.IncidentLifecycleEvents.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).ToArrayAsync(TestContext.Current.CancellationToken)); Assert.Empty(await verify.NotificationSuppressionContexts.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10bH1SelectsOneDueCauseInDueRequestedCauseIdOrder()
    {
        await using var fixture = await CreateFixtureAsync();
        var resultA = Guid.Parse("60000000-0000-0000-0000-000000000001");
        var resultB = Guid.Parse("60000000-0000-0000-0000-000000000002");
        var resultC = Guid.Parse("60000000-0000-0000-0000-000000000003");
        var resultD = Guid.Parse("60000000-0000-0000-0000-000000000004");
        var causeAId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var causeBId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var causeCId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var causeDId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var h0 = (await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken)).AddMinutes(-2);
        var h1 = h0.AddTicks(10);
        var eventA = fixture.Now;
        var eventB = fixture.Now.AddSeconds(1);
        var eventC = fixture.Now.AddSeconds(2);
        var eventD = fixture.Now.AddSeconds(3);

        await SetHeartbeatAsync(fixture, fixture.AgentId, h0);
        await AddLedgerAsync(fixture, eventA, eventA, 3, 0m, resultId: resultA);
        await using (var processA = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultA, (await new ProbeResultStatusProcessor(processA, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        await SetHeartbeatAsync(fixture, fixture.AgentId, h1);
        foreach (var source in new[] { (resultB, eventB), (resultC, eventC), (resultD, eventD) })
        {
            await AddLedgerAsync(fixture, source.Item2, source.Item2, 3, 0m, resultId: source.Item1);
            await using var process = new EePulseDbContext(fixture.Options);
            Assert.Equal(source.Item1, (await new ProbeResultStatusProcessor(process, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        }

        DateTimeOffset tieRequestedAt;
        await using (var beforeNormalize = new EePulseDbContext(fixture.Options))
        {
            var a = await beforeNormalize.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceResultId == resultA, TestContext.Current.CancellationToken);
            var b = await beforeNormalize.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceResultId == resultB, TestContext.Current.CancellationToken);
            var c = await beforeNormalize.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceResultId == resultC, TestContext.Current.CancellationToken);
            var d = await beforeNormalize.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.SourceResultId == resultD, TestContext.Current.CancellationToken);
            Assert.Equal(h0.AddSeconds(60), a.DueAt); Assert.Equal(h1.AddSeconds(60), b.DueAt); Assert.Equal(b.DueAt, c.DueAt); Assert.Equal(b.DueAt, d.DueAt); Assert.True(a.DueAt < b.DueAt);
            tieRequestedAt = await ReadPostgresTimestampStrictlyAfterAsync(fixture.ConnectionString, b.RequestedAt, TestContext.Current.CancellationToken);
            Assert.True(b.RequestedAt < tieRequestedAt);
        }

        Exception? primary = null;
        try
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (var disable = new NpgsqlCommand("ALTER TABLE \"public\".\"probe_heartbeat_expiry_causes\" DISABLE TRIGGER \"tr_probe_heartbeat_expiry_causes_append_only\"", connection))
                await disable.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            foreach (var update in new[] { (CauseId: causeAId, ResultId: resultA, RequestedAt: (DateTimeOffset?)null), (CauseId: causeBId, ResultId: resultB, RequestedAt: (DateTimeOffset?)null), (CauseId: causeCId, ResultId: resultC, RequestedAt: (DateTimeOffset?)tieRequestedAt), (CauseId: causeDId, ResultId: resultD, RequestedAt: (DateTimeOffset?)tieRequestedAt) })
            {
                await using var command = new NpgsqlCommand(update.RequestedAt.HasValue
                    ? "UPDATE \"public\".\"probe_heartbeat_expiry_causes\" SET \"cause_id\" = @causeId, \"requested_at\" = @requestedAt WHERE \"source_result_id\" = @resultId"
                    : "UPDATE \"public\".\"probe_heartbeat_expiry_causes\" SET \"cause_id\" = @causeId WHERE \"source_result_id\" = @resultId", connection);
                command.Parameters.AddWithValue("causeId", update.CauseId); command.Parameters.AddWithValue("resultId", update.ResultId);
                if (update.RequestedAt.HasValue) command.Parameters.AddWithValue("requestedAt", update.RequestedAt.Value);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var connection = new NpgsqlConnection(fixture.ConnectionString);
                await connection.OpenAsync(cleanup.Token);
                await using var enable = new NpgsqlCommand("ALTER TABLE \"public\".\"probe_heartbeat_expiry_causes\" ENABLE TRIGGER \"tr_probe_heartbeat_expiry_causes_append_only\"", connection);
                await enable.ExecuteNonQueryAsync(cleanup.Token);
            }
            catch (Exception cleanupFailure)
            {
                if (primary is not null) primary.Data["H1 cause ordering trigger cleanup failure"] = cleanupFailure;
                else throw;
            }
        }

        await using (var verifyOrdering = new EePulseDbContext(fixture.Options))
        {
            var a = await verifyOrdering.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.CauseId == causeAId, TestContext.Current.CancellationToken);
            var b = await verifyOrdering.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.CauseId == causeBId, TestContext.Current.CancellationToken);
            var c = await verifyOrdering.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.CauseId == causeCId, TestContext.Current.CancellationToken);
            var d = await verifyOrdering.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(x => x.CauseId == causeDId, TestContext.Current.CancellationToken);
            Assert.True(a.DueAt < b.DueAt); Assert.Equal(b.DueAt, c.DueAt); Assert.Equal(b.DueAt, d.DueAt); Assert.True(b.RequestedAt < c.RequestedAt); Assert.Equal(c.RequestedAt, d.RequestedAt); Assert.True(c.CauseId.CompareTo(d.CauseId) < 0);
            foreach (var source in new[] { (a, resultA, eventA, h0), (b, resultB, eventB, h1), (c, resultC, eventC, h1), (d, resultD, eventD, h1) })
                Assert.Equal((ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, ProbeResultProcessingDispositionKind.StateDriving, fixture.ProbeId, fixture.AgentId, source.Item2, source.Item3, source.Item4, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, source.Item4.AddSeconds(60)), (source.Item1.CauseType, source.Item1.SourceDisposition, source.Item1.ProbeId, source.Item1.AuthorityAgentId, source.Item1.SourceResultId, source.Item1.SourceCursorEventAt, source.Item1.SourceLastHeartbeatReceivedAt, source.Item1.SourceHeartbeatIntervalSeconds, source.Item1.SourceConfigurationVersion, source.Item1.SourceAgentGroupId, source.Item1.PolicySnapshotId, source.Item1.PolicyVersion, source.Item1.DueAt));
            var dueBound = await ReadPostgresTimestampAsync(fixture.ConnectionString, TestContext.Current.CancellationToken);
            Assert.True(a.DueAt <= dueBound && b.DueAt <= dueBound && c.DueAt <= dueBound && d.DueAt <= dueBound);
            Assert.False(await verifyOrdering.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken));
            var projection = await verifyOrdering.ProbeStatusProjections.AsNoTracking().SingleAsync(x => x.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal((fixture.AgentId, resultD, eventD, eventD), (projection.WatermarkAgentId, projection.WatermarkResultId, projection.WatermarkEventAt, projection.LastFreshEventAt));
        }

        foreach (var expected in new[] { (CauseId: causeAId, Remaining: new[] { causeBId, causeCId, causeDId }), (CauseId: causeBId, Remaining: new[] { causeCId, causeDId }), (CauseId: causeCId, Remaining: new[] { causeDId }) })
        {
            var before = await CaptureH1NoMutationSnapshotAsync(fixture);
            ProbeHeartbeatExpiryProcessorOutcome outcome;
            await using (var processor = new EePulseDbContext(fixture.Options)) outcome = await new ProbeHeartbeatExpiryCauseProcessor(processor).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            await using var verify = new EePulseDbContext(fixture.Options);
            var disposition = await verify.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == expected.CauseId, TestContext.Current.CancellationToken);
            Assert.Equal((ProbeHeartbeatExpiryProcessorOutcomeKind.NoOp, expected.CauseId, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode), (outcome.Kind, outcome.CauseId, outcome.DispositionOutcome, outcome.ReasonCode));
            Assert.Equal((expected.CauseId, fixture.ProbeId, fixture.PolicyId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode, (DateTimeOffset?)null), (disposition.CauseId, disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode, disposition.AppliedAt));
            Assert.False(await verify.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().AnyAsync(x => x.CauseId == expected.CauseId, TestContext.Current.CancellationToken));
            var undisposed = await verify.ProbeHeartbeatExpiryCauses.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !verify.ProbeHeartbeatExpiryCauseDispositions.Any(d => d.CauseId == x.CauseId)).Select(x => x.CauseId).ToArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal(expected.Remaining.Length, undisposed.Length); Assert.All(expected.Remaining, causeId => Assert.Contains(causeId, undisposed));
            var post = await CaptureH1NoMutationSnapshotAsync(fixture);
            AssertH1NoOpDelta(before, post, expected.CauseId, fixture.ProbeId, fixture.PolicyId, ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSupersededReasonCode, disposition.ExpiryCutoffReceivedAt);
        }

        await using var final = new EePulseDbContext(fixture.Options);
        Assert.False(await final.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().AnyAsync(x => x.CauseId == causeDId, TestContext.Current.CancellationToken));
        Assert.False(await final.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().AnyAsync(x => x.CauseId == causeDId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St10bResultCreatesHeartbeatCauseAndH1AppliesOnlyVisibleUnknown()
    {
        await using var fixture = await CreateFixtureAsync();
        var heartbeatAt = fixture.Now.AddMinutes(-2);
        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            var agent = await seed.Agents.SingleAsync(x => x.Id == fixture.AgentId, TestContext.Current.CancellationToken);
            agent.Heartbeat("1.0.0", "processor", 0, AgentSelfHealth.Healthy, 1, heartbeatAt, heartbeatAt);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var results = new EePulseDbContext(fixture.Options))
            Assert.Equal(resultId, (await new ProbeResultStatusProcessor(results, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);

        await using (var verifyCause = new EePulseDbContext(fixture.Options))
        {
            var cause = await verifyCause.ProbeHeartbeatExpiryCauses.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal((fixture.ProbeId, fixture.AgentId, resultId, fixture.Now, heartbeatAt, 20, 1L, fixture.GroupId, fixture.PolicyId, 1, ProbeResultProcessingDispositionKind.StateDriving),
                (cause.ProbeId, cause.AuthorityAgentId, cause.SourceResultId, cause.SourceCursorEventAt, cause.SourceLastHeartbeatReceivedAt, cause.SourceHeartbeatIntervalSeconds, cause.SourceConfigurationVersion, cause.SourceAgentGroupId, cause.PolicySnapshotId, cause.PolicyVersion, cause.SourceDisposition));
            Assert.Equal(heartbeatAt.AddSeconds(60), cause.DueAt);
            Assert.NotEqual(default, cause.RequestedAt);
        }

        await using (var expiry = new EePulseDbContext(fixture.Options))
            Assert.Equal(ProbeHeartbeatExpiryProcessorOutcomeKind.Applied, (await new ProbeHeartbeatExpiryCauseProcessor(expiry).ProcessNextDueAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        await using var final = new EePulseDbContext(fixture.Options);
        var projection = await final.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal((ProbeStatus.Up, ProbeStatus.Unknown, 0, 1, 2L), (projection.UnderlyingStatus, projection.VisibleStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.StateVersion));
        Assert.Equal(ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, (await final.ProbeHeartbeatExpiryCauseDispositions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal("agent-heartbeat-expired", (await final.ProbeHeartbeatExpiryCauseTransitions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
    }

    private static async Task<Fixture> CreateFixtureAsync(bool includeBinding = true, bool includeBoundary = true)
    {
        var postgres = await PostgresTestDatabase.StartAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migration = new EePulseDbContext(options)) await migration.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var site = new Site(Guid.NewGuid(), "P" + Guid.NewGuid().ToString("N")[..5], "Processor", "UTC", now);
        var group = new AgentGroup(Guid.NewGuid(), "Processor", null, now);
        var device = new Device(Guid.NewGuid(), site.Id, "Processor", "192.0.2.70", null, "Server", null, null, Criticality.Normal, [], now);
        var probe = new Probe(Guid.NewGuid(), device.Id, group.Id, 30, 2000, 3, 500, null, 2, 2);
        var agent = new EePulse.Domain.Agents.Agent(Guid.NewGuid(), group.Id, Guid.NewGuid(), "processor", "1.0.0", 20, now);
        var configuration = new AgentConfigurationSnapshot(group.Id, 1, FreshnessPayload(probe.Id, probe.IntervalSeconds), new byte[32], now, null);
        var acknowledgement = new AgentConfigurationAcknowledgement(Guid.NewGuid(), agent.Id, 1, AgentAcknowledgementStatus.Applied, now, now, now, null, 1, 1);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 2, 2, 500, null, now);
        var boundary = new AgentConfigurationEffectiveBoundary(agent.Id, 1, acknowledgement.Id, AgentAcknowledgementStatus.Applied, acknowledgement.ReceivedAt);
        await using (var db = new EePulseDbContext(options))
        {
            db.AddRange(site, group, device, probe, agent, configuration, acknowledgement, policy);
            if (includeBoundary) db.Add(boundary);
            if (includeBinding) db.Add(new ProbeStatusPolicyBinding(probe.Id, 1, group.Id, policy.Id));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        return new(postgres, postgres.ConnectionString, options, now, group.Id, probe.Id, agent.Id, policy.Id);
    }

    private static async Task<(Guid PolicyId, DateTimeOffset Boundary)> AddConfigurationVersionAsync(Fixture fixture, long version, int failureThreshold, int recoveryThreshold)
    {
        var at = fixture.Now.AddMinutes(1);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), (int)version, failureThreshold, recoveryThreshold, null, null, at);
        await using var db = new EePulseDbContext(fixture.Options);
        var intervalSeconds = await db.Probes.Where(probe => probe.Id == fixture.ProbeId)
            .Select(probe => probe.IntervalSeconds).SingleAsync(TestContext.Current.CancellationToken);
        var configuration = new AgentConfigurationSnapshot(fixture.GroupId, version, FreshnessPayload(fixture.ProbeId, intervalSeconds), new byte[32], at, null);
        var acknowledgement = new AgentConfigurationAcknowledgement(Guid.NewGuid(), fixture.AgentId, version, AgentAcknowledgementStatus.Applied, at, at, at, null, version, version);
        db.AddRange(configuration, acknowledgement, policy,
            new AgentConfigurationEffectiveBoundary(fixture.AgentId, version, acknowledgement.Id, AgentAcknowledgementStatus.Applied, acknowledgement.ReceivedAt),
            new ProbeStatusPolicyBinding(fixture.ProbeId, version, fixture.GroupId, policy.Id));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (policy.Id, acknowledgement.ReceivedAt);
    }

    private static async Task<DateTimeOffset> ReadBoundaryAsync(Fixture fixture, Guid agentId, long version)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.AgentConfigurationEffectiveBoundaries.Where(row => row.AgentId == agentId && row.ConfigurationVersion == version)
            .Select(row => row.AppliedAcknowledgementReceivedAt).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> AddSecondAgentAsync(Fixture fixture, DateTimeOffset heartbeatAt)
    {
        var agentId = Guid.NewGuid(); var acknowledgementId = Guid.NewGuid();
        await using var db = new EePulseDbContext(fixture.Options);
        var agent = new EePulse.Domain.Agents.Agent(agentId, fixture.GroupId, Guid.NewGuid(), "processor-b", "1.0.0", 20, fixture.Now);
        agent.SetDesiredConfiguration(1); agent.Heartbeat("1.0.0", "processor-b", 0, AgentSelfHealth.Healthy, 1, heartbeatAt, heartbeatAt);
        var boundaryAt = fixture.Now;
        var acknowledgement = new AgentConfigurationAcknowledgement(acknowledgementId, agentId, 1, AgentAcknowledgementStatus.Applied, boundaryAt, boundaryAt, boundaryAt, null, 1, 1);
        db.AddRange(agent, acknowledgement, new AgentConfigurationEffectiveBoundary(agentId, 1, acknowledgementId, AgentAcknowledgementStatus.Applied, boundaryAt));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken); return agentId;
    }

    private static async Task SetHeartbeatAsync(Fixture fixture, Guid agentId, DateTimeOffset heartbeatAt)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var agent = await db.Agents.SingleAsync(x => x.Id == agentId, TestContext.Current.CancellationToken);
        agent.Heartbeat(agent.AgentVersion, agent.MachineName, 0, AgentSelfHealth.Healthy,
            agent.DesiredConfigurationVersion, heartbeatAt, heartbeatAt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ProbeResultLedgerEntry> ReadLedgerAsync(Fixture fixture, Guid resultId)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.ProbeResultLedgerEntries.AsNoTracking()
            .SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
    }

    private static async Task<T4B4AgentEvidence> CaptureT4B4AgentEvidenceAsync(Fixture fixture, Guid agentId)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.Agents.AsNoTracking().Where(x => x.Id == agentId).Select(x => new T4B4AgentEvidence(x.Id, x.ClientInstanceId, x.Name, x.MachineName, x.AgentVersion, x.AgentGroupId, x.SelfHealth, x.Status, x.QueueDepth, x.LastHeartbeatAt, x.LastReportedAt, x.HeartbeatIntervalSeconds, x.DesiredConfigurationVersion, x.LastAppliedConfigurationVersion, x.LastConfigurationAcknowledgedAt, x.ClockSkewSuspected, x.CredentialExpiresAt, x.CreatedAt, x.RevokedAt, x.RevocationReason, x.RowVersion)).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<LedgerOrder[]> ReadPendingOrderAsync(Fixture fixture) { await using var db = new EePulseDbContext(fixture.Options); return await db.ProbeResultLedgerEntries.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId && !db.ProbeResultProcessingDispositions.Any(d => d.AgentId == x.AgentId && d.ResultId == x.ResultId)).OrderBy(x => x.EndedAt).ThenBy(x => x.AgentId).ThenBy(x => x.ResultId).Select(x => new LedgerOrder(x.AgentId, x.ResultId, x.EndedAt, x.ReceivedAt)).ToArrayAsync(TestContext.Current.CancellationToken); }
    private static async Task<ProbeArtifacts> ReadProbeArtifactsAsync(Fixture fixture) { await using var db = new EePulseDbContext(fixture.Options); var incidents = await db.AvailabilityIncidents.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).Select(x => new IncidentArtifact(x.Id, x.ProbeId, x.RuleKey, x.Status, x.OpenedAt, x.AcknowledgedAt, x.AcknowledgedBy, x.AcknowledgementComment, x.ResolvedAt, x.ResolvedBy, x.ResolutionNote, x.OccurrenceCount)).OrderBy(x => x.Id).ToArrayAsync(TestContext.Current.CancellationToken); var events = await db.IncidentLifecycleEvents.AsNoTracking().Where(x => x.ProbeId == fixture.ProbeId).Select(x => new EventArtifact(x.EventId, x.IncidentId, x.ProbeId, x.SourceAgentId, x.SourceResultId, x.SourceFromStatus, x.SourceToStatus, x.SourceReasonCode, x.PolicySnapshotId, x.PolicyVersion, x.LifecycleEventType, x.LifecycleEventKey, x.ProcessingDisposition, x.OccurredAt)).OrderBy(x => x.EventId).ToArrayAsync(TestContext.Current.CancellationToken); var eventIds = events.Select(x => x.EventId); var contexts = await db.NotificationSuppressionContexts.AsNoTracking().Where(x => eventIds.Contains(x.EventId)).Select(x => new ContextArtifact(x.EventId, x.IncidentId, x.LifecycleEventKey, x.PolicyVersion, x.Eligibility, x.ReasonCode, x.EvaluatedAt)).OrderBy(x => x.EventId).ToArrayAsync(TestContext.Current.CancellationToken); return new(incidents, events, contexts); }
    private static async Task<Guid> AddLedgerAsync(Fixture fixture, DateTimeOffset endedAt, DateTimeOffset receivedAt, int successes, decimal packetLossRatio, long configurationVersion = 1, Guid? resultId = null, decimal? averageRtt = 1m, Guid? agentId = null)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var id = resultId ?? Guid.NewGuid();
        db.Add(CreateLedger(fixture, endedAt, receivedAt, successes, packetLossRatio, configurationVersion, id, averageRtt, agentId ?? fixture.AgentId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static ProbeResultLedgerEntry CreateLedger(Fixture fixture, DateTimeOffset endedAt, DateTimeOffset receivedAt, int successes, decimal packetLossRatio, long configurationVersion, Guid resultId, decimal? averageRtt, Guid? agentId = null) =>
        new(agentId ?? fixture.AgentId, resultId, fixture.ProbeId, configurationVersion,
            endedAt.AddSeconds(-1), endedAt, 3, successes, packetLossRatio, averageRtt, averageRtt, averageRtt, null, new byte[32], receivedAt);

    private static string FreshnessPayload(Guid probeId, int intervalSeconds) =>
        $$"""{"probes":[{"probeId":"{{probeId:D}}","intervalSeconds":{{intervalSeconds}}}]}""";

    private static async Task<DateTimeOffset> ReadPostgresTimestampAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        var timestamp = Assert.IsType<DateTime>(await command.ExecuteScalarAsync(cancellationToken));
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
        var value = new DateTimeOffset(timestamp);
        return new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    }

    private static async Task<DateTimeOffset> ReadPostgresTimestampStrictlyAfterAsync(string connectionString, DateTimeOffset lowerBound, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var value = await ReadPostgresTimestampAsync(connectionString, bounded.Token);
            if (value > lowerBound) return value;
            await Task.Delay(TimeSpan.FromMilliseconds(1), bounded.Token);
        }
    }

    private static async Task RunHeartbeatAsync(EePulseDbContext db, Guid probeId, Action<ProbeHeartbeatExpiryProcessorOutcome> setOutcome, CancellationToken cancellationToken) => setOutcome(await new ProbeHeartbeatExpiryCauseProcessor(db).ProcessNextDueAsync(probeId, cancellationToken));
    private static async Task RunFreshnessAsync(EePulseDbContext db, Guid probeId, Action<ProbeFreshnessExpiryProcessorOutcome> setOutcome, CancellationToken cancellationToken) => setOutcome(await new ProbeFreshnessExpiryCauseProcessor(db).ProcessNextDueAsync(probeId, cancellationToken));
    private static async Task RunResultAsync(EePulseDbContext db, Guid probeId, DateTimeOffset now, Action<ProbeResultStatusProcessorOutcome> setOutcome, CancellationToken cancellationToken) => setOutcome(await new ProbeResultStatusProcessor(db, new FixedClock(now)).ProcessNextAsync(probeId, cancellationToken));

    private static async Task<int> LockProjectionForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid probeId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT probe_id FROM public.probe_status_projections WHERE probe_id = @probeId FOR UPDATE", connection, transaction); command.Parameters.AddWithValue("probeId", probeId);
        Assert.Equal(probeId, Assert.IsType<Guid>(await command.ExecuteScalarAsync(cancellationToken))); return await GetBackendPidAsync(connection, cancellationToken);
    }

    private static async Task<int> LockProbeAdvisoryTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid probeId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@probeId, 0))", connection, transaction);
        command.Parameters.AddWithValue("probeId", probeId.ToString("D")); await command.ExecuteNonQueryAsync(cancellationToken); return await GetBackendPidAsync(connection, cancellationToken);
    }

    private static async Task WaitForH1ProbeBlockedByAsync(NpgsqlConnection observer, T4B3BackendIdentity waitingBackend, int blockerPid, Guid probeId, Task task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; var canonical = probeId.ToString("D"); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                attempts++; await using var command = new NpgsqlCommand("""
            SELECT a.state, a.wait_event_type, a.wait_event,
                   EXISTS (SELECT 1 FROM pg_locks WHERE pid=@pid AND locktype='advisory' AND NOT granted AND ((CASE WHEN classid::bigint >= 2147483648 THEN classid::bigint-4294967296 ELSE classid::bigint END)*4294967296)+objid::bigint=hashtextextended(@probeId,0)),
                   @blocker=ANY(pg_blocking_pids(@pid)),
                   COALESCE((SELECT string_agg(format('classid=%s,objid=%s,objsubid=%s,mode=%s,granted=%s,identity=%s',classid::bigint,objid::bigint,objsubid,mode,granted,((CASE WHEN classid::bigint >= 2147483648 THEN classid::bigint-4294967296 ELSE classid::bigint END)*4294967296)+objid::bigint),' | ' ORDER BY classid,objid,objsubid,mode,granted) FROM pg_locks WHERE pid=@pid AND locktype='advisory'),'<none>'),
                   COALESCE(array_to_string(pg_blocking_pids(@pid),','),'<none>'), hashtextextended(@probeId,0)
            FROM pg_stat_activity a WHERE a.pid=@pid
            """, observer); command.Parameters.AddWithValue("pid", waitingBackend.Pid); command.Parameters.AddWithValue("blocker", blockerPid); command.Parameters.AddWithValue("probeId", canonical); await using var reader = await command.ExecuteReaderAsync(timeout.Token);
                if (await reader.ReadAsync(timeout.Token)) { last = $"backend={waitingBackend};state={reader.GetString(0)};wait={reader.IsDBNull(1) ? "<null>":reader.GetString(1)}/{(reader.IsDBNull(2) ? "<null>" : reader.GetString(2))};blocking={reader.GetString(6)};expectedAdvisoryIdentity={reader.GetInt64(7)};observedAdvisoryRows={reader.GetString(5)}"; if (string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), "Lock", StringComparison.Ordinal) && reader.GetBoolean(3) && reader.GetBoolean(4)) return; } else last = "<missing>";
                if (task.IsCompleted) { await task; throw new Xunit.Sdk.XunitException($"T4B4 H1 completed before waiting on the exact Probe advisory lock. backend={waitingBackend};blocker={blockerPid};probe={canonical};{last};attempts={attempts};elapsed={DateTimeOffset.UtcNow - started}."); }
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for T4B4 H1 Probe-lock evidence. backend={waitingBackend};blocker={blockerPid};probe={canonical};{last};attempts={attempts};elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private sealed record T4B3BackendIdentity(int Pid, string ApplicationName, string BackendStartedAt, string DatabaseName);
    private sealed record T4B4AgentEvidence(Guid Id, Guid ClientInstanceId, string Name, string MachineName, string AgentVersion, Guid AgentGroupId, AgentSelfHealth SelfHealth, AgentStatus Status, long QueueDepth, DateTimeOffset? LastHeartbeatAt, DateTimeOffset? LastReportedAt, int HeartbeatIntervalSeconds, long DesiredConfigurationVersion, long LastAppliedConfigurationVersion, DateTimeOffset? LastConfigurationAcknowledgedAt, bool ClockSkewSuspected, DateTimeOffset? CredentialExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt, string? RevocationReason, long RowVersion);
    private sealed record T4B3DeferredResource(string Name, Func<Task> DisposeAsync);
    private sealed record T4B3DeferredDiagnostic(string OperationId, string Stage, string Outcome, Exception? Exception);
    private static readonly object T4B3DeferredGate = new();
    private static readonly Dictionary<string, T4B3DeferredOwner> T4B3DeferredOwners = [];
    private static readonly Dictionary<string, T4B3DeferredDiagnosticHolder> T4B3DeferredDiagnostics = [];
    private sealed class T4B3DeferredDiagnosticHolder
    {
        private readonly object sync = new(); private readonly List<T4B3DeferredDiagnostic> values = [];
        public void Add(T4B3DeferredDiagnostic value) { lock (sync) values.Add(value); }
        public IReadOnlyList<T4B3DeferredDiagnostic> Snapshot() { lock (sync) return values.ToArray(); }
    }

    private static async Task<T4B3BackendIdentity> CaptureT4B3BackendIdentityAsync(NpgsqlConnection observer, string applicationName, Task task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { while (true) { attempts++; await using var command = new NpgsqlCommand("SELECT pid, application_name, backend_start::text, datname FROM pg_stat_activity WHERE application_name=@name AND state <> 'idle' ORDER BY pid", observer); command.Parameters.AddWithValue("name", applicationName); await using var reader = await command.ExecuteReaderAsync(timeout.Token); var rows = new List<T4B3BackendIdentity>(); while (await reader.ReadAsync(timeout.Token)) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))); last = rows.Count == 0 ? "<none>" : string.Join(" | ", rows.Select(x => $"pid={x.Pid},application={x.ApplicationName},backend_start={x.BackendStartedAt},database={x.DatabaseName}")); if (rows.Count == 1) return rows[0]; if (task.IsCompleted) { await task; throw new Xunit.Sdk.XunitException($"T4B3 processor completed before one backend identity was observable. application={applicationName}; backends={last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); } await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token); } }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for T4B3 backend identity. application={applicationName}; backends={last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private static async Task<T4B3BackendIdentity> CaptureT4B3IdleBackendIdentityAsync(NpgsqlConnection observer, string applicationName, int pid, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; var commandStage = "not-started"; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { while (true) { attempts++; commandStage = "started"; await using var command = new NpgsqlCommand("SELECT pid, application_name, backend_start::text, datname, state FROM pg_stat_activity WHERE pid=@pid", observer); command.Parameters.AddWithValue("pid", pid); await using var reader = await command.ExecuteReaderAsync(timeout.Token); commandStage = "reader-opened"; if (await reader.ReadAsync(timeout.Token)) { commandStage = "row-read"; var observedPid = reader.GetInt32(0); var observedApplication = reader.GetString(1); var observedStarted = reader.GetString(2); var observedDatabase = reader.GetString(3); var observedState = reader.GetString(4); last = $"pid={observedPid};application={observedApplication};backend_start={observedStarted};database={observedDatabase};state={observedState}"; if (observedPid == pid && observedApplication == applicationName && observedState == "idle in transaction") return new(observedPid, observedApplication, observedStarted, observedDatabase); } else { commandStage = "completed-without-row"; last = "<absent>"; } await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token); } }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out capturing idle T4B4 waiter E backend identity. expectedPid={pid}; expectedApplication={applicationName}; lastObserved={last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}; observerCommandStage={commandStage}."); }
    }

    private static async Task WaitForT4B4WaiterEAsync(NpgsqlConnection observer, T4B3BackendIdentity waiter, T4B3BackendIdentity blocker, int excludedProbeBlocker, Guid probeId, Task waiterTask, Task h1Task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                attempts++; await using var command = new NpgsqlCommand("""
            SELECT a.application_name, a.backend_start::text, a.datname, a.state, a.wait_event_type, a.wait_event,
                   EXISTS(SELECT 1 FROM pg_locks WHERE pid=@pid AND locktype='transactionid' AND NOT granted),
                   @blocker=ANY(pg_blocking_pids(@pid)), NOT(@excluded=ANY(pg_blocking_pids(@pid))),
                   COALESCE(array_to_string(pg_blocking_pids(@pid),','),'<none>')
            FROM pg_stat_activity a WHERE a.pid=@pid
            """, observer); command.Parameters.AddWithValue("pid", waiter.Pid); command.Parameters.AddWithValue("blocker", blocker.Pid); command.Parameters.AddWithValue("excluded", excludedProbeBlocker); await using var reader = await command.ExecuteReaderAsync(timeout.Token);
                if (await reader.ReadAsync(timeout.Token)) { var identity = reader.GetString(0) == waiter.ApplicationName && reader.GetString(1) == waiter.BackendStartedAt && reader.GetString(2) == waiter.DatabaseName; last = $"expected={waiter};observed={reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)};state={reader.GetString(3)};wait={reader.IsDBNull(4) ? "<null>":reader.GetString(4)}/{(reader.IsDBNull(5) ? "<null>" : reader.GetString(5)};blocking={reader.GetString(9)}"; if (identity && string.Equals(reader.IsDBNull(4) ? null : reader.GetString(4), "Lock", StringComparison.Ordinal) && reader.GetBoolean(6) && reader.GetBoolean(7) && reader.GetBoolean(8)) return; } else last = "<missing>";
                if (waiterTask.IsCompleted || h1Task.IsCompleted) { if (waiterTask.IsCompleted) await waiterTask; if (h1Task.IsCompleted) await h1Task; throw new Xunit.Sdk.XunitException($"T4B4 waiter E lock evidence was not observed. {last};attempts={attempts};elapsed={DateTimeOffset.UtcNow - started}."); }
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for T4B4 waiter E identity/lock evidence. {last};attempts={attempts};elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private static async Task<bool> SettleT4B3TaskAsync(string name, Task? task, CancellationTokenSource cancellation, T4B3BackendIdentity? backend, NpgsqlConnection observer, List<Exception> failures)
    {
        if (task is null || await ObserveTerminalAsync(name, task, false, failures)) return true;
        try { cancellation.Cancel(); } catch (Exception exception) { failures.Add(new InvalidOperationException($"T4B3 cleanup failed while canceling {name}'s dedicated token.", exception)); }
        var terminalAfterDedicatedCancellation = await ObserveTerminalAsync($"{name} after dedicated cancellation", task, true, failures);
        if (terminalAfterDedicatedCancellation) return true;
        if (backend is null) failures.Add(new InvalidOperationException($"T4B3 cleanup has no immutable backend identity for {name}.")); else await SignalT4B3BackendAsync($"canceling {name}", observer, backend, false, failures);
        if (await ObserveTerminalAsync($"{name} after pg_cancel_backend", task, true, failures)) return true;
        if (backend is null) failures.Add(new InvalidOperationException($"T4B3 cleanup has no immutable backend identity for {name}.")); else await SignalT4B3BackendAsync($"terminating {name}", observer, backend, true, failures);
        return await ObserveTerminalAsync($"{name} after pg_terminate_backend", task, true, failures);
    }

    private static async Task SignalT4B3BackendAsync(string stage, NpgsqlConnection observer, T4B3BackendIdentity expected, bool terminate, List<Exception> failures)
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var operation = terminate ? "pg_terminate_backend" : "pg_cancel_backend"; await using var command = new NpgsqlCommand($"""
            WITH current_backend AS (SELECT pid, application_name, backend_start::text AS backend_start, datname FROM pg_stat_activity WHERE pid=@pid),
            matched_backend AS (SELECT pid FROM current_backend WHERE application_name=@applicationName AND backend_start=@backendStartedAt AND datname=@databaseName),
            signal AS (SELECT {operation}(pid) AS result FROM matched_backend)
            SELECT EXISTS(SELECT 1 FROM current_backend), (SELECT application_name || '|' || backend_start || '|' || datname FROM current_backend), EXISTS(SELECT 1 FROM matched_backend), (SELECT result FROM signal)
            """, observer); command.Parameters.AddWithValue("pid", expected.Pid); command.Parameters.AddWithValue("applicationName", expected.ApplicationName); command.Parameters.AddWithValue("backendStartedAt", expected.BackendStartedAt); command.Parameters.AddWithValue("databaseName", expected.DatabaseName); await using var reader = await command.ExecuteReaderAsync(timeout.Token); if (!await reader.ReadAsync(timeout.Token)) throw new Xunit.Sdk.XunitException($"T4B3 conditional signal returned no row. expected={expected}."); var exists = reader.GetBoolean(0); var actual = reader.IsDBNull(1) ? "<absent>" : reader.GetString(1); var attempted = reader.GetBoolean(2); var result = reader.IsDBNull(3) ? (bool?)null : reader.GetBoolean(3); if (!attempted || result != true) failures.Add(new InvalidOperationException($"T4B3 cleanup {stage} did not signal an exact backend. expected={expected}; pidExists={exists}; actual={actual}; signalAttempted={attempted}; signalResult={result}.")); }
        catch (Exception exception) { failures.Add(new InvalidOperationException($"T4B3 cleanup failed while {stage}.", exception)); }
    }

    private static void TransferT4B3Ownership(string name, Task task, CancellationTokenSource cancellation, T4B3BackendIdentity? backend, IReadOnlyList<T4B3DeferredResource> resources, List<Exception> failures, Exception? primary)
    {
        var id = $"t4b3-deferred-{Guid.NewGuid():N}"; var diagnostics = new T4B3DeferredDiagnosticHolder(); var hard = new InvalidOperationException($"T4B3 cleanup could not settle {name}; ownership transferred. operationId={id}."); hard.Data[$"T4B3Deferred:{id}"] = diagnostics; if (primary is not null) primary.Data[$"T4B3Deferred:{id}"] = diagnostics; failures.Add(hard); var owner = new T4B3DeferredOwner(id, name, task, cancellation, backend, resources, diagnostics); owner.Start();
    }

    private sealed class T4B3DeferredOwner(string id, string name, Task task, CancellationTokenSource cancellation, T4B3BackendIdentity? backend, IReadOnlyList<T4B3DeferredResource> resources, T4B3DeferredDiagnosticHolder diagnostics)
    {
        private readonly TaskCompletionSource<bool> startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CompletionTask { get; private set; } = Task.CompletedTask;
        public void Start() { lock (T4B3DeferredGate) { T4B3DeferredOwners.Add(id, this); T4B3DeferredDiagnostics[id] = diagnostics; CompletionTask = CompleteAsync(); if (!T4B3DeferredOwners.TryGetValue(id, out var rooted) || !ReferenceEquals(rooted, this)) throw new InvalidOperationException($"T4B3 deferred registry lost {id} before task assignment."); } startGate.TrySetResult(true); }
        private async Task CompleteAsync() { await startGate.Task.ConfigureAwait(false); try { try { await task.ConfigureAwait(false); diagnostics.Add(new(id, "task", "succeeded", null)); } catch (OperationCanceledException exception) { diagnostics.Add(new(id, "task", "canceled", exception)); } catch (Exception exception) { foreach (var inner in exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception]) diagnostics.Add(new(id, "task", "faulted", inner)); } foreach (var resource in resources) try { await resource.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { diagnostics.Add(new(id, resource.Name, "dispose-failed", exception)); } } catch (Exception exception) { diagnostics.Add(new(id, "runner", "faulted", exception)); } finally { try { cancellation.Dispose(); } catch (Exception exception) { diagnostics.Add(new(id, "cancellation", "dispose-failed", exception)); } lock (T4B3DeferredGate) { T4B3DeferredDiagnostics[id] = diagnostics; T4B3DeferredOwners.Remove(id); } _ = backend; } }
    }

    private static async Task WaitForGrantedProbeAndProjectionWaitAsync(NpgsqlConnection observer, T4B3BackendIdentity waitingBackend, int blockerPid, Guid probeId, Task task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; var canonical = probeId.ToString("D"); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                attempts++; await using var command = new NpgsqlCommand("""
            SELECT a.state, a.wait_event_type, a.wait_event,
                   EXISTS (SELECT 1 FROM pg_locks WHERE pid=@pid AND locktype='advisory' AND granted AND (lpad(to_hex(classid::bigint),8,'0') || lpad(to_hex(objid::bigint),8,'0'))=lpad(to_hex(hashtextextended(@probeId,0)),16,'0')),
                   EXISTS (SELECT 1 FROM pg_locks WHERE pid=@pid AND locktype='transactionid' AND NOT granted),
                   @blocker = ANY(pg_blocking_pids(@pid)),
                   COALESCE((SELECT string_agg(locktype || ':' || mode || ':' || granted::text, ', ' ORDER BY locktype,mode,granted) FROM pg_locks WHERE pid=@pid AND locktype IN ('transactionid','advisory')),'<none>'),
                   COALESCE(array_to_string(pg_blocking_pids(@pid),','),'<none>'),
                   hashtextextended(@probeId,0),
                   COALESCE((SELECT string_agg(format('classid=%s,objid=%s,objsubid=%s,mode=%s,granted=%s,identity=%s', classid::bigint, objid::bigint, objsubid, mode, granted, ((CASE WHEN classid::bigint >= 2147483648 THEN classid::bigint - 4294967296 ELSE classid::bigint END) * 4294967296) + objid::bigint), ' | ' ORDER BY classid,objid,objsubid,mode,granted) FROM pg_locks WHERE pid=@pid AND locktype='advisory'),'<none>')
            FROM pg_stat_activity a WHERE a.pid=@pid
            """, observer); command.Parameters.AddWithValue("pid", waitingBackend.Pid); command.Parameters.AddWithValue("blocker", blockerPid); command.Parameters.AddWithValue("probeId", canonical); await using var reader = await command.ExecuteReaderAsync(timeout.Token);
                if (await reader.ReadAsync(timeout.Token)) { last = $"backend={waitingBackend};state={reader.GetString(0)},wait={reader.GetString(1)},event={reader.GetString(2)},locks={reader.GetString(6)},blocking={reader.GetString(7)},expectedAdvisoryIdentity={reader.GetInt64(8)},observedAdvisoryRows={reader.GetString(9)}"; if (string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), "Lock", StringComparison.Ordinal) && reader.GetBoolean(3) && reader.GetBoolean(4) && reader.GetBoolean(5)) return; } else last = "<missing>";
                if (task.IsCompleted) { await task; throw new Xunit.Sdk.XunitException($"Result processor did not retain the exact Probe advisory lock while waiting on the projection. backend={waitingBackend}; blocker={blockerPid}; probe={canonical}; {last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for result-processor Probe ownership and projection wait. backend={waitingBackend}; blocker={blockerPid}; probe={canonical}; {last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private static async Task WaitForProbeAdvisoryBlockedByAsync(NpgsqlConnection observer, T4B3BackendIdentity waitingBackend, T4B3BackendIdentity blockerBackend, int excludedBlockerPid, Guid probeId, Task task, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow; var attempts = 0; var last = "<none>"; var canonical = probeId.ToString("D"); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                attempts++; await using var command = new NpgsqlCommand("""
            SELECT a.state, a.wait_event_type, a.wait_event,
                   EXISTS (SELECT 1 FROM pg_locks WHERE pid=@pid AND locktype='advisory' AND NOT granted AND (lpad(to_hex(classid::bigint),8,'0') || lpad(to_hex(objid::bigint),8,'0'))=lpad(to_hex(hashtextextended(@probeId,0)),16,'0')),
                   @blocker = ANY(pg_blocking_pids(@pid)), NOT (@excluded = ANY(pg_blocking_pids(@pid))),
                   COALESCE((SELECT string_agg(locktype || ':' || mode || ':' || granted::text, ', ' ORDER BY locktype,mode,granted) FROM pg_locks WHERE pid=@pid AND locktype IN ('transactionid','advisory')),'<none>'), COALESCE(array_to_string(pg_blocking_pids(@pid),','),'<none>'),
                   hashtextextended(@probeId,0),
                   COALESCE((SELECT string_agg(format('classid=%s,objid=%s,objsubid=%s,mode=%s,granted=%s,identity=%s', classid::bigint, objid::bigint, objsubid, mode, granted, ((CASE WHEN classid::bigint >= 2147483648 THEN classid::bigint - 4294967296 ELSE classid::bigint END) * 4294967296) + objid::bigint), ' | ' ORDER BY classid,objid,objsubid,mode,granted) FROM pg_locks WHERE pid=@pid AND locktype='advisory'),'<none>')
            FROM pg_stat_activity a WHERE a.pid=@pid
            """, observer); command.Parameters.AddWithValue("pid", waitingBackend.Pid); command.Parameters.AddWithValue("blocker", blockerBackend.Pid); command.Parameters.AddWithValue("excluded", excludedBlockerPid); command.Parameters.AddWithValue("probeId", canonical); await using var reader = await command.ExecuteReaderAsync(timeout.Token);
                if (await reader.ReadAsync(timeout.Token)) { last = $"backend={waitingBackend};blocker={blockerBackend};state={reader.GetString(0)},wait={reader.GetString(1)},event={reader.GetString(2)},locks={reader.GetString(6)},blocking={reader.GetString(7)},expectedAdvisoryIdentity={reader.GetInt64(8)},observedAdvisoryRows={reader.GetString(9)}"; if (string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), "Lock", StringComparison.Ordinal) && reader.GetBoolean(3) && reader.GetBoolean(4) && reader.GetBoolean(5)) return; } else last = "<missing>";
                if (task.IsCompleted) { await task; throw new Xunit.Sdk.XunitException($"H1 did not wait on result processor's exact Probe advisory lock. backend={waitingBackend}; blocker={blockerBackend}; excluded={excludedBlockerPid}; probe={canonical}; {last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested) { throw new Xunit.Sdk.XunitException($"Timed out waiting for H1 Probe advisory evidence. backend={waitingBackend}; blocker={blockerBackend}; excluded={excludedBlockerPid}; probe={canonical}; {last}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - started}."); }
    }

    private static async Task<int> LockAgentForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid agentId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM agents WHERE id = @agentId FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("agentId", agentId);
        Assert.Equal(agentId, Assert.IsType<Guid>(await command.ExecuteScalarAsync(cancellationToken)));
        return await GetBackendPidAsync(connection, cancellationToken);
    }

    private static async Task LockAgentForUpdateWithoutFollowUpCommandAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid agentId, Action<NpgsqlCommand> captureCommand, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM agents WHERE id = @agentId FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("agentId", agentId);
        captureCommand(command);
        Assert.Equal(agentId, Assert.IsType<Guid>(await command.ExecuteScalarAsync(cancellationToken)));
    }

    private static async Task<int> GetBackendPidAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return Assert.IsType<int>(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> ObserveTerminalAsync(string name, Task task, bool expectedCancellation, List<Exception> cleanupFailures)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await task.WaitAsync(cleanup.Token);
            return true;
        }
        catch (OperationCanceledException) when (task.IsCompleted && expectedCancellation)
        {
            return true;
        }
        catch (Exception cleanupFailure) when (task.IsCompleted)
        {
            cleanupFailures.Add(new InvalidOperationException($"T4A1 cleanup observed {name} complete with an unexpected failure.", cleanupFailure));
            return true;
        }
        catch (Exception cleanupFailure)
        {
            cleanupFailures.Add(new InvalidOperationException($"T4A1 cleanup timed out while awaiting {name} to a terminal state.", cleanupFailure));
            return false;
        }
    }

    private static async Task SignalBackendAsync(NpgsqlConnection observer, int backendPid, bool terminate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(terminate ? "SELECT pg_terminate_backend(@backendPid)" : "SELECT pg_cancel_backend(@backendPid)", observer);
        command.Parameters.AddWithValue("backendPid", backendPid);
        if (Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken))) return;
        throw new Xunit.Sdk.XunitException($"T4A1 cleanup could not {(terminate ? "terminate" : "cancel")} the exact test-owned backend PID {backendPid}.");
    }

    private static async Task<int> WaitForBackendPidAsync(NpgsqlConnection observer, string applicationName, Task invocation, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var attempts = 0;
        var lastObserved = "<none>";
        using var localTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        localTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                attempts++;
                await using var command = new NpgsqlCommand("SELECT pid, state, wait_event_type, wait_event FROM pg_stat_activity WHERE application_name = @applicationName ORDER BY pid", observer);
                command.Parameters.AddWithValue("applicationName", applicationName);
                var observed = new List<(int Pid, string Description)>();
                await using var reader = await command.ExecuteReaderAsync(localTimeout.Token);
                while (await reader.ReadAsync(localTimeout.Token))
                    observed.Add((reader.GetInt32(0), $"pid={reader.GetInt32(0)},state={reader.GetString(1)},wait_event_type={(reader.IsDBNull(2) ? "<null>" : reader.GetString(2))},wait_event={(reader.IsDBNull(3) ? "<null>" : reader.GetString(3))}"));
                lastObserved = observed.Count == 0 ? "<none>" : string.Join(" | ", observed.Select(row => row.Description));
                if (observed.Count == 1) return observed[0].Pid;
                if (invocation.IsCompleted) { await invocation; throw new Xunit.Sdk.XunitException($"The expiry processor completed before its backend PID was observable. ApplicationName={applicationName}; lastBackends={lastObserved}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - startedAt}."); }
                await Task.Delay(TimeSpan.FromMilliseconds(20), localTimeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && localTimeout.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for exactly one backend PID. ApplicationName={applicationName}; lastBackends={lastObserved}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - startedAt}.");
        }
    }

    private static async Task WaitForTransactionLockEvidenceAsync(NpgsqlConnection observer, int waitingPid, int blockerPid, bool requireNoAdvisoryLock, Task waitingTask, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var attempts = 0;
        var lastState = "<missing>";
        var lastWaitEventType = "<missing>";
        var lastWaitEvent = "<missing>";
        var lastLockRows = "<none>";
        var lastBlockingPids = "<none>";
        using var localTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        localTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                attempts++;
                await using var command = new NpgsqlCommand("""
                    SELECT a.state,
                           a.wait_event_type,
                           a.wait_event,
                           EXISTS (SELECT 1 FROM pg_locks WHERE pid = @waitingPid AND locktype = 'transactionid' AND NOT granted),
                           @blockerPid = ANY(pg_blocking_pids(@waitingPid)),
                           NOT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @waitingPid AND locktype = 'advisory'),
                           COALESCE((SELECT string_agg(locktype || ':' || mode || ':' || granted::text, ', ' ORDER BY locktype, mode, granted) FROM pg_locks WHERE pid = @waitingPid AND locktype IN ('transactionid', 'advisory')), '<none>'),
                           COALESCE(array_to_string(pg_blocking_pids(@waitingPid), ','), '<none>')
                    FROM pg_stat_activity a WHERE a.pid = @waitingPid
                    """, observer);
                command.Parameters.AddWithValue("waitingPid", waitingPid); command.Parameters.AddWithValue("blockerPid", blockerPid);
                await using var reader = await command.ExecuteReaderAsync(localTimeout.Token);
                if (await reader.ReadAsync(localTimeout.Token))
                {
                    lastState = reader.GetString(0); lastWaitEventType = reader.IsDBNull(1) ? "<null>" : reader.GetString(1); lastWaitEvent = reader.IsDBNull(2) ? "<null>" : reader.GetString(2);
                    var evidence = (string.Equals(lastWaitEventType, "Lock", StringComparison.Ordinal), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5));
                    lastLockRows = reader.GetString(6); lastBlockingPids = reader.GetString(7);
                    if (evidence.Item1 && evidence.Item2 && evidence.Item3 && (!requireNoAdvisoryLock || evidence.Item4)) return;
                }
                else { lastState = "<missing>"; lastWaitEventType = "<missing>"; lastWaitEvent = "<missing>"; lastLockRows = "<none>"; lastBlockingPids = "<none>"; }
                if (waitingTask.IsCompleted) { await waitingTask; throw new Xunit.Sdk.XunitException($"The expected PostgreSQL transaction-id wait was not observed. waiterPid={waitingPid}; blockerPid={blockerPid}; state={lastState}; wait_event_type={lastWaitEventType}; wait_event={lastWaitEvent}; locks={lastLockRows}; blockingPids={lastBlockingPids}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - startedAt}."); }
                await Task.Delay(TimeSpan.FromMilliseconds(20), localTimeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && localTimeout.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for PostgreSQL transaction-id lock evidence. waiterPid={waitingPid}; blockerPid={blockerPid}; requireNoAdvisoryLock={requireNoAdvisoryLock}; state={lastState}; wait_event_type={lastWaitEventType}; wait_event={lastWaitEvent}; locks={lastLockRows}; blockingPids={lastBlockingPids}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - startedAt}.");
        }
    }

    private static async Task WaitForUngrantedProbeLockAsync(NpgsqlConnection observer, int backendProcessId, Guid probeId, Task acquiring, CancellationToken cancellationToken)
    {
        var canonicalProbeId = probeId.ToString("D");
        var startedAt = DateTimeOffset.UtcNow;
        var attempts = 0;
        var lastState = "<missing>";
        var lastWaitEventType = "<missing>";
        var lastWaitEvent = "<missing>";
        var lastLockRows = "<none>";
        var lastBlockingPids = "<none>";
        using var localTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        localTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                attempts++;
                await using var command = new NpgsqlCommand("""
                    SELECT a.state,
                           a.wait_event_type,
                           a.wait_event,
                           EXISTS (
                               SELECT 1 FROM pg_locks
                               WHERE locktype = 'advisory' AND pid = @backendProcessId AND NOT granted
                                 AND (lpad(to_hex(classid::bigint), 8, '0') || lpad(to_hex(objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0')),
                           COALESCE((SELECT string_agg(locktype || ':' || mode || ':' || granted::text, ', ' ORDER BY locktype, mode, granted) FROM pg_locks WHERE pid = @backendProcessId AND locktype IN ('transactionid', 'advisory')), '<none>'),
                           COALESCE(array_to_string(pg_blocking_pids(@backendProcessId), ','), '<none>')
                    FROM pg_stat_activity a WHERE a.pid = @backendProcessId
                    """, observer);
                command.Parameters.AddWithValue("backendProcessId", backendProcessId);
                command.Parameters.AddWithValue("probeId", canonicalProbeId);
                await using var reader = await command.ExecuteReaderAsync(localTimeout.Token);
                if (await reader.ReadAsync(localTimeout.Token))
                {
                    lastState = reader.GetString(0); lastWaitEventType = reader.IsDBNull(1) ? "<null>" : reader.GetString(1); lastWaitEvent = reader.IsDBNull(2) ? "<null>" : reader.GetString(2);
                    lastLockRows = reader.GetString(4); lastBlockingPids = reader.GetString(5);
                    if (reader.GetBoolean(3)) return;
                }
                else { lastState = "<missing>"; lastWaitEventType = "<missing>"; lastWaitEvent = "<missing>"; lastLockRows = "<none>"; lastBlockingPids = "<none>"; }
                if (acquiring.IsCompleted)
                {
                    await acquiring;
                    throw new Xunit.Sdk.XunitException($"The ingestion transaction acquired the Probe lock before the processor released it. backendPid={backendProcessId}; probeId={canonicalProbeId}; state={lastState}; wait_event_type={lastWaitEventType}; wait_event={lastWaitEvent}; locks={lastLockRows}; blockingPids={lastBlockingPids}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - startedAt}.");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), localTimeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && localTimeout.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for the ungranted Probe advisory lock. backendPid={backendProcessId}; probeId={canonicalProbeId}; state={lastState}; wait_event_type={lastWaitEventType}; wait_event={lastWaitEvent}; locks={lastLockRows}; blockingPids={lastBlockingPids}; attempts={attempts}; elapsed={DateTimeOffset.UtcNow - startedAt}.");
        }
    }

    private sealed record ResultIdentity(Guid AgentId, Guid ResultId);
    private sealed record LedgerOrder(Guid AgentId, Guid ResultId, DateTimeOffset EndedAt, DateTimeOffset ReceivedAt);
    private sealed record ProbeHeartbeatExpiryCauseSnapshot(Guid CauseId, Guid ProbeId, ProbeHeartbeatExpiryCauseType CauseType, Guid AuthorityAgentId, Guid SourceResultId, DateTimeOffset SourceCursorEventAt, DateTimeOffset SourceLastHeartbeatAt, int SourceHeartbeatIntervalSeconds, long SourceConfigurationVersion, Guid SourceAgentGroupId, ProbeResultProcessingDispositionKind SourceDisposition, Guid PolicySnapshotId, int PolicyVersion, DateTimeOffset DueAt, DateTimeOffset RequestedAt);
    private sealed record ProbeStatusProjectionSnapshot(Guid ProbeId, ProbeStatus UnderlyingStatus, ProbeStatus VisibleStatus, int ConsecutiveFailureCount, int ConsecutiveSuccessCount, long StateVersion, Guid? WatermarkAgentId, Guid? WatermarkResultId, DateTimeOffset? WatermarkEventAt, DateTimeOffset? LastFreshEventAt, Guid? OpenIncidentId);
    private sealed record AgentShareCommand(ImmutableArray<string> AgentIds);
    private sealed record IncidentArtifact(Guid Id, Guid ProbeId, string RuleKey, AvailabilityIncidentStatus Status, DateTimeOffset OpenedAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, string? AcknowledgementComment, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? ResolutionNote, int OccurrenceCount);
    private sealed record EventArtifact(Guid EventId, Guid IncidentId, Guid ProbeId, Guid SourceAgentId, Guid SourceResultId, ProbeStatus SourceFromStatus, ProbeStatus SourceToStatus, string SourceReasonCode, Guid PolicySnapshotId, int PolicyVersion, IncidentLifecycleEventType LifecycleEventType, string LifecycleEventKey, ProbeResultProcessingDispositionKind ProcessingDisposition, DateTimeOffset OccurredAt);
    private sealed record ContextArtifact(Guid EventId, Guid IncidentId, string LifecycleEventKey, int PolicyVersion, NotificationSuppressionEligibility Eligibility, string ReasonCode, DateTimeOffset EvaluatedAt);
    private sealed record ProbeArtifacts(IncidentArtifact[] Incidents, EventArtifact[] Events, ContextArtifact[] Contexts);
    private sealed record IncidentSnapshot(Guid Id, AvailabilityIncidentStatus Status, int OccurrenceCount);
    private sealed record ProjectionSnapshot(ProbeStatus UnderlyingStatus, int ConsecutiveFailureCount, int ConsecutiveSuccessCount,
        DateTimeOffset? LastFreshEventAt, DateTimeOffset? WatermarkEventAt, Guid? WatermarkAgentId, Guid? WatermarkResultId,
        long StateVersion, Guid? OpenIncidentId);
    private sealed record IncidentStateSnapshot(Guid Id, AvailabilityIncidentStatus Status, DateTimeOffset OpenedAt,
        DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, string? AcknowledgementComment, DateTimeOffset? ResolvedAt,
        string? ResolvedBy, string? ResolutionNote, int OccurrenceCount);
    private sealed record DisabledNonMutationSnapshot(ProjectionSnapshot? Projection, string Incidents, string Transitions,
        string Events, string Contexts);
    private sealed record FreshnessCauseIdentity(Guid SourceAgentId, Guid SourceResultId, Guid ProbeId, DateTimeOffset SourceCursorEventAt);
    private sealed record St09cRollbackSnapshot(ProjectionSnapshot Projection, ResultIdentity[] Dispositions,
        ResultIdentity[] Transitions, IncidentStateSnapshot[] Incidents, Guid[] EventIds, Guid[] ContextIds,
        FreshnessCauseIdentity[] Causes);
    private sealed record St10ProjectionSnapshot(Guid ProbeId, ProbeStatus UnderlyingStatus, ProbeStatus VisibleStatus,
        int ConsecutiveFailureCount, int ConsecutiveSuccessCount, DateTimeOffset? LastFreshEventAt,
        DateTimeOffset? WatermarkEventAt, Guid? WatermarkAgentId, Guid? WatermarkResultId, long StateVersion,
        Guid? OpenIncidentId);
    private sealed record St10CauseSnapshot(Guid CauseId, Guid ProbeId, Guid SourceAgentId, Guid SourceResultId,
        DateTimeOffset SourceCursorEventAt, DateTimeOffset SourceLastFreshEventAt, long SourceConfigurationVersion,
        Guid SourceAgentGroupId, Guid PolicySnapshotId, int PolicyVersion, int FreshnessIntervalSeconds,
        int FreshnessGraceSeconds, DateTimeOffset DueAt, DateTimeOffset RequestedAt);
    private sealed record St10ExpiryDispositionSnapshot(Guid CauseId, Guid ProbeId, Guid PolicySnapshotId,
        int PolicyVersion, ProbeFreshnessExpiryCauseDispositionOutcome Outcome, string ReasonCode,
        DateTimeOffset ExpiryCutoffReceivedAt, DateTimeOffset? AppliedAt);
    private sealed record St10ResultDispositionSnapshot(Guid AgentId, Guid ResultId, Guid ProbeId, DateTimeOffset EventAt,
        ProbeResultProcessingDispositionKind Disposition, string ReasonCode, Guid? ResolvedPolicySnapshotId,
        int? ResolvedPolicyVersion, DateTimeOffset DecidedAt);
    private sealed record St10ResultTransitionSnapshot(Guid AgentId, Guid ResultId, Guid ProbeId, ProbeStatus FromStatus,
        ProbeStatus ToStatus, string ReasonCode, DateTimeOffset EventAt, DateTimeOffset ReceivedAt,
        ProbeResultProcessingDispositionKind ProcessingDisposition);
    private sealed record St10ExpiryTransitionSnapshot(Guid CauseId, Guid ProbeId, Guid PolicySnapshotId,
        int PolicyVersion, ProbeFreshnessExpiryCauseDispositionOutcome DispositionOutcome, ProbeStatus FromVisibleStatus,
        ProbeStatus ToVisibleStatus, string ReasonCode, DateTimeOffset AppliedAt);
    private sealed record St10RollbackSnapshot(St10ProjectionSnapshot Projection, St10ResultDispositionSnapshot[] ResultDispositions,
        St10ResultTransitionSnapshot[] ResultTransitions, IncidentStateSnapshot[] Incidents, Guid[] EventIds, Guid[] ContextIds,
        St10CauseSnapshot[] Causes, St10ExpiryDispositionSnapshot[] ExpiryDispositions,
        St10ExpiryTransitionSnapshot[] ExpiryTransitions);
    private sealed record H1HeartbeatCauseSourceEvidence(Guid CauseId, Guid AuthorityAgentId, Guid SourceResultId,
        DateTimeOffset SourceCursorEventAt, DateTimeOffset SourceLastHeartbeatReceivedAt, int SourceHeartbeatIntervalSeconds,
        long SourceConfigurationVersion, Guid SourceAgentGroupId, Guid PolicySnapshotId, int PolicyVersion);
    private sealed record H1ProjectionSnapshot(Guid ProbeId, ProbeStatus UnderlyingStatus, ProbeStatus VisibleStatus,
        int ConsecutiveFailureCount, int ConsecutiveSuccessCount, long StateVersion, Guid? WatermarkAgentId,
        Guid? WatermarkResultId, DateTimeOffset? WatermarkEventAt, DateTimeOffset? LastFreshEventAt, Guid? OpenIncidentId);
    private sealed record H1NoOpPrerequisiteEvidence(H1HeartbeatCauseSourceEvidence ControlledCause,
        H1ProjectionSnapshot PreMutationProjection, Guid? SuccessorResultId, DateTimeOffset? SuccessorEventAt,
        DateTimeOffset? SuccessorReceiptAt, DateTimeOffset? SuccessorLastFreshEventAt, Guid? SuccessorCauseId,
        DateTimeOffset? SuccessorRequestedAtLowerBound, DateTimeOffset? SuccessorRequestedAtUpperBound,
        DateTimeOffset OriginalHeartbeatAt, int OriginalHeartbeatIntervalSeconds, DateTimeOffset? AdvancedHeartbeatAt,
        int? AdvancedHeartbeatIntervalSeconds, H1ProjectionSnapshot? ExpectedPreH1Projection,
        DateTimeOffset? PreH1PostgresBound, Guid[] ExpectedDueCauseIds);
    private sealed record DueHeartbeatCause(Guid CauseId, Guid ResultId, DateTimeOffset EventAt, DateTimeOffset DueAt, Guid? OpenIncidentId);
    private sealed record FreshnessFullSnapshot(Guid CauseId, Guid ProbeId, ProbeFreshnessExpiryCauseType CauseType, Guid SourceAgentId, Guid SourceResultId, DateTimeOffset SourceCursorEventAt, DateTimeOffset SourceLastFreshEventAt, long SourceConfigurationVersion, Guid SourceAgentGroupId, ProbeResultProcessingDispositionKind SourceDisposition, Guid PolicySnapshotId, int PolicyVersion, int FreshnessIntervalSeconds, int FreshnessGraceSeconds, DateTimeOffset DueAt, DateTimeOffset RequestedAt);
    private sealed record HeartbeatFullSnapshot(Guid CauseId, Guid ProbeId, ProbeHeartbeatExpiryCauseType CauseType, Guid AuthorityAgentId, Guid SourceResultId, DateTimeOffset SourceCursorEventAt, DateTimeOffset SourceLastHeartbeatReceivedAt, int SourceHeartbeatIntervalSeconds, long SourceConfigurationVersion, Guid SourceAgentGroupId, ProbeResultProcessingDispositionKind SourceDisposition, Guid PolicySnapshotId, int PolicyVersion, DateTimeOffset DueAt, DateTimeOffset RequestedAt);
    private sealed record HeartbeatDispositionFullSnapshot(Guid CauseId, Guid ProbeId, Guid PolicySnapshotId, int PolicyVersion, ProbeHeartbeatExpiryCauseDispositionOutcome Outcome, string ReasonCode, DateTimeOffset ExpiryCutoffReceivedAt, DateTimeOffset? AppliedAt);
    private sealed record HeartbeatTransitionFullSnapshot(Guid CauseId, Guid ProbeId, Guid PolicySnapshotId, int PolicyVersion, ProbeHeartbeatExpiryCauseDispositionOutcome DispositionOutcome, ProbeStatus FromVisibleStatus, ProbeStatus ToVisibleStatus, string ReasonCode, DateTimeOffset AppliedAt);
    private sealed record H1LedgerFullSnapshot(Guid AgentId, Guid ResultId, Guid ProbeId, long ConfigurationVersion,
        DateTimeOffset StartedAt, DateTimeOffset EndedAt, int AttemptCount, int SuccessfulAttemptCount, decimal PacketLossRatio,
        decimal? MinRttMilliseconds, decimal? AverageRttMilliseconds, decimal? MaxRttMilliseconds, string? ErrorCategory,
        string ImmutablePayloadDigest, DateTimeOffset ReceivedAt);
    private sealed record H1NoMutationSnapshot(St10ProjectionSnapshot? Projection, H1LedgerFullSnapshot[] Ledger, St10ResultDispositionSnapshot[] ResultDispositions,
        St10ResultTransitionSnapshot[] ResultTransitions, FreshnessFullSnapshot[] FreshnessCauses, HeartbeatFullSnapshot[] HeartbeatCauses, HeartbeatDispositionFullSnapshot[] Dispositions,
        HeartbeatTransitionFullSnapshot[] Transitions, ProbeArtifacts Artifacts);
    private sealed record OccurrenceRollbackSnapshot(ProbeStatus UnderlyingStatus, int ConsecutiveFailureCount,
        int ConsecutiveSuccessCount, DateTimeOffset? LastFreshEventAt, DateTimeOffset? WatermarkEventAt,
        Guid? WatermarkAgentId, Guid? WatermarkResultId, long StateVersion, Guid? OpenIncidentId,
        IncidentSnapshot[] Incidents, ResultIdentity[] Dispositions, ResultIdentity[] Transitions,
        Guid[] EventIds, Guid[] ContextIds);
    private sealed record Fixture(IAsyncDisposable Postgres, string ConnectionString, DbContextOptions<EePulseDbContext> Options, DateTimeOffset Now, Guid GroupId, Guid ProbeId, Guid AgentId, Guid PolicyId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Postgres.DisposeAsync();
    }
    private sealed class FixedClock(DateTimeOffset now) : IUtcClock { public DateTimeOffset UtcNow => now; }
    private sealed class ThrowBeforeSaveInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) => throw new InvalidOperationException("test pre-commit failure");
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("test pre-commit failure"));
    }
    private sealed class FirstAgentShareGate : DbCommandInterceptor
    {
        private int used;
        private readonly object sync = new();
        private readonly List<AgentShareCommand> agentShareCommands = [];
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TriggerCount => Volatile.Read(ref used);
        public ImmutableArray<AgentShareCommand> AgentShareCommands { get { lock (sync) return agentShareCommands.ToImmutableArray(); } }
        public Task WaitUntilEnteredAsync(CancellationToken cancellationToken) => Reached.Task.WaitAsync(cancellationToken);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM agents", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("FOR SHARE", StringComparison.OrdinalIgnoreCase))
            {
                var requestedAgentIds = new List<string>();
                foreach (DbParameter parameter in command.Parameters)
                {
                    if (parameter.Value is Guid agentId) requestedAgentIds.Add(agentId.ToString("D"));
                    else if (parameter.Value is Guid[] agentIds) requestedAgentIds.AddRange(agentIds.Select(agentId => agentId.ToString("D")));
                    else if (parameter.Value is Array array) foreach (var value in array) if (value is Guid agentId) requestedAgentIds.Add(agentId.ToString("D"));
                }
                lock (sync) agentShareCommands.Add(new AgentShareCommand(requestedAgentIds.ToImmutableArray()));
                if (Interlocked.CompareExchange(ref used, 1, 0) == 0) { Reached.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
    private sealed class ThrowOnSecondSaveInterceptor : SaveChangesInterceptor
    {
        private int saves;
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (Interlocked.Increment(ref saves) == 2) throw new InvalidOperationException("test second-save failure");
            return result;
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref saves) == 2) return ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("test second-save failure"));
            return ValueTask.FromResult(result);
        }
    }
    private sealed class BlockingSaveInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
    private sealed class ThrowOnHeartbeatStateVersionUpdateInterceptor : DbCommandInterceptor
    {
        private int triggerCount;
        public bool WasTriggered => Volatile.Read(ref triggerCount) != 0;
        public int TriggerCount => Volatile.Read(ref triggerCount);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.CommandText.Contains("UPDATE probe_status_projections SET state_version = state_version + 1 WHERE probe_id", StringComparison.OrdinalIgnoreCase) && Interlocked.CompareExchange(ref triggerCount, 1, 0) == 0)
                throw new InvalidOperationException("st10b-h1-final-state-version-flush-failure");
            return ValueTask.FromResult(result);
        }
    }
}
