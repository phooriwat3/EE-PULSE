using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EePulse.Api.Authorization;
using EePulse.Application.Time;
using EePulse.Contracts.Dashboard;
using EePulse.Domain.Auditing;
using EePulse.Domain.Identity;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Data;

namespace EePulse.Api.Dashboard;

internal enum IncidentCommandRoute
{
    Acknowledge,
    AddComment,
    Resolve,
}

internal sealed class IncidentCommandInput<TCommand>
    where TCommand : notnull
{
    internal IncidentCommandInput(
        IncidentCommandRoute route,
        Guid incidentId,
        string idempotencyKey,
        PrincipalIdentity identity,
        string parsedStrongIfMatch,
        ReadOnlySpan<byte> canonicalRequestBodyUtf8,
        string trustedCorrelationId,
        TCommand command)
    {
        if (!Enum.IsDefined(route)) throw new ArgumentOutOfRangeException(nameof(route));
        if (incidentId == Guid.Empty) throw new ArgumentException("Incident ID is required.", nameof(incidentId));
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentNullException.ThrowIfNull(identity);
        if (!IncidentConcurrencyContract.IsStrongOpaqueEtag(parsedStrongIfMatch))
            throw new ArgumentException("If-Match must be a strong opaque ETag.", nameof(parsedStrongIfMatch));
        if (canonicalRequestBodyUtf8.IsEmpty)
            throw new ArgumentException("Canonical request bytes are required.", nameof(canonicalRequestBodyUtf8));
        IncidentRuntimeBoundaryValidation.RequireSafeCorrelationId(trustedCorrelationId, nameof(trustedCorrelationId));
        ArgumentNullException.ThrowIfNull(command);

        Route = route;
        IncidentId = incidentId;
        IdempotencyKey = idempotencyKey;
        Identity = identity;
        ParsedStrongIfMatch = parsedStrongIfMatch;
        CanonicalRequestBodyUtf8 = Copy(canonicalRequestBodyUtf8);
        TrustedCorrelationId = trustedCorrelationId;
        Command = command;
    }

    public IncidentCommandRoute Route { get; }
    public Guid IncidentId { get; }
    public string IdempotencyKey { get; }
    public PrincipalIdentity Identity { get; }
    public string ParsedStrongIfMatch { get; }
    public ImmutableArray<byte> CanonicalRequestBodyUtf8 { get; }
    public string TrustedCorrelationId { get; }
    public TCommand Command { get; }

    private static ImmutableArray<byte> Copy(ReadOnlySpan<byte> value) => ImmutableArray.CreateRange(value.ToArray());
}

internal sealed class StoredIncidentHttpResponse
{
    internal StoredIncidentHttpResponse(int statusCode, ReadOnlySpan<byte> bodyUtf8, string contentType, string etag)
    {
        if (statusCode is not (StatusCodes.Status200OK or StatusCodes.Status201Created))
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        if (bodyUtf8.IsEmpty) throw new ArgumentException("Response bytes are required.", nameof(bodyUtf8));
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        if (!IncidentConcurrencyContract.IsStrongOpaqueEtag(etag))
            throw new ArgumentException("ETag must be strong and opaque.", nameof(etag));

        StatusCode = statusCode;
        BodyUtf8 = ImmutableArray.CreateRange(bodyUtf8.ToArray());
        ContentType = contentType;
        Etag = etag;
    }

    public int StatusCode { get; }
    public ImmutableArray<byte> BodyUtf8 { get; }
    public string ContentType { get; }
    public string Etag { get; }
}

internal sealed class IncidentCommandResponseContext
{
    internal IncidentCommandResponseContext(Guid incidentId, string resultingEtag, string trustedCorrelationId, DateTimeOffset completedAt)
    {
        IncidentRuntimeBoundaryValidation.RequireNonEmptyGuid(incidentId, nameof(incidentId));
        IncidentRuntimeBoundaryValidation.RequireStrongEtag(resultingEtag, nameof(resultingEtag));
        IncidentRuntimeBoundaryValidation.RequireSafeCorrelationId(trustedCorrelationId, nameof(trustedCorrelationId));
        IncidentRuntimeBoundaryValidation.RequireUtc(completedAt, nameof(completedAt));

        IncidentId = incidentId;
        ResultingEtag = resultingEtag;
        TrustedCorrelationId = trustedCorrelationId;
        CompletedAt = completedAt;
    }

    public Guid IncidentId { get; }
    public string ResultingEtag { get; }
    public string TrustedCorrelationId { get; }
    public DateTimeOffset CompletedAt { get; }
}

internal sealed class StagedCommandOutcome
{
    internal StagedCommandOutcome(Guid outcomeId, string outcomeKind, DateTimeOffset completedAt)
    {
        IncidentRuntimeBoundaryValidation.RequireNonEmptyGuid(outcomeId, nameof(outcomeId));
        if (outcomeKind is not ("acknowledgement" or "general_comment" or "manual_resolution"))
            throw new ArgumentException("Outcome kind is invalid.", nameof(outcomeKind));
        IncidentRuntimeBoundaryValidation.RequireUtc(completedAt, nameof(completedAt));

        OutcomeId = outcomeId;
        OutcomeKind = outcomeKind;
        CompletedAt = completedAt;
    }

    public Guid OutcomeId { get; }
    public string OutcomeKind { get; }
    public DateTimeOffset CompletedAt { get; }
}

internal abstract class LockedCommandDecision<TConflict>
    where TConflict : notnull
{
    internal sealed class Apply : LockedCommandDecision<TConflict>
    {
        internal Apply(StagedCommandOutcome outcome) => Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        public StagedCommandOutcome Outcome { get; }
    }

    internal sealed class StateConflict : LockedCommandDecision<TConflict>
    {
        internal StateConflict(TConflict conflict) => Conflict = conflict ?? throw new ArgumentNullException(nameof(conflict));
        public TConflict Conflict { get; }
    }
}

internal abstract class IncidentCommandResult<TConflict>
    where TConflict : notnull
{
    internal sealed class Replay : IncidentCommandResult<TConflict>
    {
        internal Replay(StoredIncidentHttpResponse response) => Response = response ?? throw new ArgumentNullException(nameof(response));
        public StoredIncidentHttpResponse Response { get; }
    }

    internal sealed class FreshSuccess : IncidentCommandResult<TConflict>
    {
        internal FreshSuccess(StoredIncidentHttpResponse response) => Response = response ?? throw new ArgumentNullException(nameof(response));
        public StoredIncidentHttpResponse Response { get; }
    }

    internal sealed class FreshPreconditionFailed : IncidentCommandResult<TConflict>
    {
        internal FreshPreconditionFailed(string currentEtag)
        {
            IncidentRuntimeBoundaryValidation.RequireStrongEtag(currentEtag, nameof(currentEtag));
            CurrentEtag = currentEtag;
        }

        public string CurrentEtag { get; }
    }

    internal sealed class FreshStateConflict : IncidentCommandResult<TConflict>
    {
        internal FreshStateConflict(TConflict conflict, string currentEtag)
        {
            Conflict = conflict ?? throw new ArgumentNullException(nameof(conflict));
            IncidentRuntimeBoundaryValidation.RequireStrongEtag(currentEtag, nameof(currentEtag));
            CurrentEtag = currentEtag;
        }

        public TConflict Conflict { get; }
        public string CurrentEtag { get; }
    }

    internal sealed class IdempotencyReuseConflict : IncidentCommandResult<TConflict>;

    internal sealed class NotFound : IncidentCommandResult<TConflict>;

    internal sealed class DependencyFailure : IncidentCommandResult<TConflict>;

    internal sealed class IndeterminateCommit : IncidentCommandResult<TConflict>;
}

internal interface IIncidentLockedCommandHandler<TCommand, TConflict>
    where TCommand : notnull
    where TConflict : notnull
{
    ValueTask<LockedCommandDecision<TConflict>> ApplyAfterCurrentEtagAcceptedAsync(
        LockedIncidentExecutionContext<TCommand> context,
        CancellationToken cancellationToken);

    ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
        IncidentCommandResponseContext context,
        StagedCommandOutcome outcome,
        CancellationToken cancellationToken);
}

internal interface IIncidentCommandCoordinator
{
    Task<IncidentCommandResult<TConflict>> ExecuteAsync<TCommand, TConflict>(
        IncidentCommandInput<TCommand> input,
        IIncidentLockedCommandHandler<TCommand, TConflict> handler,
        CancellationToken cancellationToken)
        where TCommand : notnull
        where TConflict : notnull;
}

/// <summary>Owns the single PostgreSQL commit acknowledgement boundary for incident commands.</summary>
internal interface IIncidentCommitBoundary
{
    Task CommitAsync(IDbContextTransaction transaction, CancellationToken cancellationToken);
}

internal sealed class IncidentCommitBoundary : IIncidentCommitBoundary
{
    public Task CommitAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>Owns transaction acquisition and safe cleanup for incident commands.</summary>
internal interface IIncidentTransactionBoundary
{
    Task<IDbContextTransaction> BeginAsync(EePulseDbContext db, CancellationToken cancellationToken);
    Task RollbackAsync(IDbContextTransaction transaction);
    ValueTask DisposeAsync(IDbContextTransaction transaction);
}

internal sealed class IncidentTransactionBoundary : IIncidentTransactionBoundary
{
    public Task<IDbContextTransaction> BeginAsync(EePulseDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    public Task RollbackAsync(IDbContextTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.RollbackAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync(IDbContextTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.DisposeAsync();
    }
}

internal interface IIncidentRequestFingerprintProvider
{
    ImmutableArray<byte> Compute(IncidentCommandRoute route, Guid incidentId, Guid actorId,
        string parsedStrongIfMatch, ReadOnlySpan<byte> canonicalRequestBodyUtf8);
    bool Matches(short storedVersion, ReadOnlySpan<byte> storedDigest, IncidentCommandRoute route,
        Guid incidentId, Guid actorId, string parsedStrongIfMatch, ReadOnlySpan<byte> canonicalRequestBodyUtf8);
}

internal sealed class IncidentRequestFingerprintV1Provider : IIncidentRequestFingerprintProvider
{
    public ImmutableArray<byte> Compute(IncidentCommandRoute route, Guid incidentId, Guid actorId,
        string parsedStrongIfMatch, ReadOnlySpan<byte> canonicalRequestBodyUtf8) =>
        IncidentRequestFingerprintV1.Compute(IncidentRequestFingerprintV1.Version, route, incidentId, actorId,
            parsedStrongIfMatch, canonicalRequestBodyUtf8);

    public bool Matches(short storedVersion, ReadOnlySpan<byte> storedDigest, IncidentCommandRoute route,
        Guid incidentId, Guid actorId, string parsedStrongIfMatch, ReadOnlySpan<byte> canonicalRequestBodyUtf8) =>
        IncidentRequestFingerprintV1.Matches(storedVersion, storedDigest, route, incidentId, actorId,
            parsedStrongIfMatch, canonicalRequestBodyUtf8);
}

internal interface IIncidentIdempotencyAdvisoryKeyProvider
{
    (int NamespaceKey, int DerivedKey) Derive(string idempotencyKey);
}

internal sealed class IncidentIdempotencyAdvisoryKeyProvider : IIncidentIdempotencyAdvisoryKeyProvider
{
    public (int NamespaceKey, int DerivedKey) Derive(string idempotencyKey) =>
        IncidentIdempotencyAdvisoryKeyDeriver.Derive(idempotencyKey);
}

internal sealed class IncidentCommandCoordinator(
    EePulseDbContext db,
    IUtcClock clock,
    IIncidentEtagProvider etags,
    IIncidentRequestFingerprintProvider fingerprints,
    IIncidentIdempotencyAdvisoryKeyProvider advisoryKeys,
    IIncidentCommitBoundary commitBoundary,
    IIncidentTransactionBoundary transactionBoundary) : IIncidentCommandCoordinator
{
    public async Task<IncidentCommandResult<TConflict>> ExecuteAsync<TCommand, TConflict>(
        IncidentCommandInput<TCommand> input,
        IIncidentLockedCommandHandler<TCommand, TConflict> handler,
        CancellationToken cancellationToken)
        where TCommand : notnull
        where TConflict : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(handler);

        IDbContextTransaction? transaction = null;
        var committed = false;
        var rollbackRequired = false;
        try
        {
            try
            {
                transaction = await transactionBoundary.BeginAsync(db, cancellationToken);
                rollbackRequired = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new IncidentCommandResult<TConflict>.DependencyFailure();
            }

            var advisoryKey = advisoryKeys.Derive(input.IdempotencyKey);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({advisoryKey.NamespaceKey}, {advisoryKey.DerivedKey})", cancellationToken);

            var replayRecord = await (
                from receipt in db.IdempotencyReceipts.AsNoTracking()
                join actor in db.HumanPrincipals.AsNoTracking() on receipt.ActorId equals actor.Id into actors
                from actor in actors.DefaultIfEmpty()
                where receipt.IdempotencyKey == input.IdempotencyKey
                select new IncidentReceiptReplayRecord(receipt, actor))
                .SingleOrDefaultAsync(cancellationToken);
            if (replayRecord is not null)
            {
                return Replay<TCommand, TConflict>(input, replayRecord);
            }

            // PostgreSQL stores timestamptz at microsecond resolution.  One normalized
            // command instant is shared by every fresh persisted row and the response,
            // so a replay and a later read cannot expose a different timestamp.
            var commandAt = NormalizeCommandTimestamp(clock.UtcNow);
            var actorId = await GetOrCreatePrincipalAsync(input.Identity, commandAt, cancellationToken);

            // This read only supplies the key for the shared WP-06 lock.  It is deliberately
            // repeated under locks below and is never used as mutation authority.
            var probeId = await db.AvailabilityIncidents.AsNoTracking()
                .Where(x => x.Id == input.IncidentId)
                .Select(x => (Guid?)x.ProbeId)
                .SingleOrDefaultAsync(cancellationToken);
            if (probeId is null)
            {
                return new IncidentCommandResult<TConflict>.NotFound();
            }

            await ProbeTransactionLock.AcquireAsync(db, probeId.Value, cancellationToken);
            var projection = await db.ProbeStatusProjections
                .FromSqlInterpolated($"SELECT * FROM probe_status_projections WHERE probe_id = {probeId.Value} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (projection is null)
            {
                return new IncidentCommandResult<TConflict>.DependencyFailure();
            }

            var incident = await db.AvailabilityIncidents
                .FromSqlInterpolated($"SELECT * FROM availability_incidents WHERE id = {input.IncidentId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (incident is null)
            {
                return new IncidentCommandResult<TConflict>.NotFound();
            }
            if (incident.ProbeId != probeId.Value)
            {
                return new IncidentCommandResult<TConflict>.DependencyFailure();
            }

            var currentEtag = etags.Create(incident.Id, incident.RowVersion);
            if (!string.Equals(input.ParsedStrongIfMatch, currentEtag, StringComparison.Ordinal))
            {
                return new IncidentCommandResult<TConflict>.FreshPreconditionFailed(currentEtag);
            }

            var occurredAt = commandAt;
            var execution = new LockedIncidentExecutionContext<TCommand>(
                input.Command, incident, projection, actorId, input.IdempotencyKey, input.TrustedCorrelationId, occurredAt);
            var decision = await handler.ApplyAfterCurrentEtagAcceptedAsync(execution, cancellationToken);
            if (decision is LockedCommandDecision<TConflict>.StateConflict conflict)
            {
                return new IncidentCommandResult<TConflict>.FreshStateConflict(conflict.Conflict, currentEtag);
            }

            var apply = (LockedCommandDecision<TConflict>.Apply)decision;
            execution.StageInto(db, apply.Outcome);
            // A general comment changes the incident's public concurrency state just as
            // the lifecycle commands do.  Mark the locked aggregate dirty and let the
            // established DbContext concurrency-token incrementer advance RowVersion.
            if (apply.Outcome.OutcomeKind == "general_comment") db.Entry(incident).State = EntityState.Modified;
            await db.SaveChangesAsync(cancellationToken);

            var resultingEtag = etags.Create(incident.Id, incident.RowVersion);
            var completedAt = commandAt;
            var response = await handler.CreateSuccessResponseAfterFirstFlushAsync(
                new IncidentCommandResponseContext(incident.Id, resultingEtag, input.TrustedCorrelationId, completedAt),
                apply.Outcome, cancellationToken);

            db.IdempotencyReceipts.Add(CreateReceipt(input, actorId, apply.Outcome, response, completedAt));
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                await commitBoundary.CommitAsync(transaction, cancellationToken);
                committed = true;
                rollbackRequired = false;
                return new IncidentCommandResult<TConflict>.FreshSuccess(response);
            }
            catch (PostgresException)
            {
                return new IncidentCommandResult<TConflict>.DependencyFailure();
            }
            catch (Exception)
            {
                // A non-server acknowledgement failure after COMMIT has been issued is not proof
                // of rollback.  Disposal releases the connection; a same-key retry resolves it.
                rollbackRequired = false;
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return new IncidentCommandResult<TConflict>.IndeterminateCommit();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only the caller's token is cancellation.  A provider-originated
            // OperationCanceledException is handled by the dependency boundary below.
            throw;
        }
        catch
        {
            return new IncidentCommandResult<TConflict>.DependencyFailure();
        }
        finally
        {
            if (transaction is not null)
            {
                if (rollbackRequired && !committed)
                {
                    try { await transactionBoundary.RollbackAsync(transaction); }
                    catch { }
                }

                try { await transactionBoundary.DisposeAsync(transaction); }
                catch { }
            }
        }
    }

    private IncidentCommandResult<TConflict> Replay<TCommand, TConflict>(
        IncidentCommandInput<TCommand> input, IncidentReceiptReplayRecord replayRecord)
        where TCommand : notnull
        where TConflict : notnull
    {
        var receipt = replayRecord.Receipt;
        var receiptActor = replayRecord.Actor;
        if (receiptActor is null || receiptActor.Id != receipt.ActorId)
        {
            return new IncidentCommandResult<TConflict>.DependencyFailure();
        }

        if (!string.Equals(receiptActor.Issuer, input.Identity.Issuer, StringComparison.Ordinal) ||
            !string.Equals(receiptActor.Subject, input.Identity.Subject, StringComparison.Ordinal))
            return new IncidentCommandResult<TConflict>.IdempotencyReuseConflict();

        if (receipt.RequestFingerprintVersion != IncidentRequestFingerprintV1.Version)
            return new IncidentCommandResult<TConflict>.DependencyFailure();

        if (receipt.RouteTemplate != IncidentRequestFingerprintV1.RouteTemplate(input.Route) ||
            receipt.IncidentId != input.IncidentId ||
            !fingerprints.Matches(receipt.RequestFingerprintVersion, receipt.RequestDigest,
                input.Route, input.IncidentId, receipt.ActorId, input.ParsedStrongIfMatch, input.CanonicalRequestBodyUtf8.AsSpan()))
        {
            return new IncidentCommandResult<TConflict>.IdempotencyReuseConflict();
        }

        return new IncidentCommandResult<TConflict>.Replay(new StoredIncidentHttpResponse(
            receipt.ResponseStatus, receipt.ResponseBody, receipt.ResponseContentType, receipt.ResponseEtag));
    }

    private static DateTimeOffset NormalizeCommandTimestamp(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("The command clock must return a UTC instant.", nameof(value));
        return Wp07DashboardCanonicalizer.NormalizePostgresTimestamp(value);
    }

    private async Task<Guid> GetOrCreatePrincipalAsync(PrincipalIdentity identity, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var candidateId = Guid.NewGuid();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = """
            INSERT INTO human_principals (id, issuer, subject, created_at)
            VALUES (@id, @issuer, @subject, @created_at)
            ON CONFLICT (issuer, subject) DO NOTHING
            RETURNING id
            """;
        AddParameter(command, "@id", candidateId);
        AddParameter(command, "@issuer", identity.Issuer);
        AddParameter(command, "@subject", identity.Subject);
        AddParameter(command, "@created_at", createdAt);
        var insertedId = await command.ExecuteScalarAsync(cancellationToken);
        if (insertedId is Guid resolvedId && resolvedId != Guid.Empty)
            return resolvedId;

        var principal = await db.HumanPrincipals.AsNoTracking().SingleOrDefaultAsync(
            x => x.Issuer == identity.Issuer && x.Subject == identity.Subject, cancellationToken);
        if (principal is null || principal.Id == Guid.Empty)
            throw new InvalidOperationException("The immutable principal mapping could not be read.");
        return principal.Id;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private IdempotencyReceipt CreateReceipt<TCommand>(IncidentCommandInput<TCommand> input, Guid actorId,
        StagedCommandOutcome outcome, StoredIncidentHttpResponse response, DateTimeOffset completedAt)
        where TCommand : notnull
    {
        var digest = fingerprints.Compute(input.Route, input.IncidentId, actorId, input.ParsedStrongIfMatch,
            input.CanonicalRequestBodyUtf8.AsSpan());
        Guid? commentId = outcome.OutcomeKind == "general_comment" ? outcome.OutcomeId : null;
        Guid? actionId = outcome.OutcomeKind == "general_comment" ? null : outcome.OutcomeId;
        return new IdempotencyReceipt(input.IdempotencyKey, IncidentRequestFingerprintV1.RouteTemplate(input.Route),
            input.IncidentId, actorId, IncidentRequestFingerprintV1.Version, digest.ToArray(), response.StatusCode,
            response.BodyUtf8.ToArray(), response.ContentType, response.Etag, outcome.OutcomeKind, commentId, actionId,
            completedAt, completedAt);
    }

}

internal sealed class IncidentReceiptReplayRecord(IdempotencyReceipt receipt, HumanPrincipal? actor)
{
    internal IdempotencyReceipt Receipt { get; } = receipt ?? throw new ArgumentNullException(nameof(receipt));
    internal HumanPrincipal? Actor { get; } = actor;
}

internal sealed class LockedIncidentExecutionContext<TCommand>
    where TCommand : notnull
{
    private readonly List<object> staged = [];
    internal LockedIncidentExecutionContext(
        TCommand command,
        AvailabilityIncident incident,
        ProbeStatusProjection projection,
        Guid actorId,
        string idempotencyKey,
        string trustedCorrelationId,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(projection);
        if (actorId == Guid.Empty) throw new ArgumentException("Actor ID is required.", nameof(actorId));
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        IncidentRuntimeBoundaryValidation.RequireSafeCorrelationId(trustedCorrelationId, nameof(trustedCorrelationId));
        IncidentRuntimeBoundaryValidation.RequireUtc(occurredAt, nameof(occurredAt));

        Command = command;
        Incident = incident;
        Projection = projection;
        ActorId = actorId;
        IdempotencyKey = idempotencyKey;
        TrustedCorrelationId = trustedCorrelationId;
        OccurredAt = occurredAt;
    }

    public TCommand Command { get; }
    public AvailabilityIncident Incident { get; }
    public ProbeStatusProjection Projection { get; }
    public Guid ActorId { get; }
    public string IdempotencyKey { get; }
    public string TrustedCorrelationId { get; }
    public DateTimeOffset OccurredAt { get; }

    internal void Stage(IncidentComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        if (comment.IncidentId != Incident.Id || comment.ProbeId != Incident.ProbeId ||
            comment.AuthorId != ActorId || !string.Equals(comment.IdempotencyKey, IdempotencyKey, StringComparison.Ordinal))
            throw new ArgumentException("The comment does not belong to this locked command.", nameof(comment));
        staged.Add(comment);
    }

    internal void Stage(IncidentLifecycleAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.IncidentId != Incident.Id || action.ProbeId != Incident.ProbeId ||
            action.ActorId != ActorId || !string.Equals(action.IdempotencyKey, IdempotencyKey, StringComparison.Ordinal))
            throw new ArgumentException("The lifecycle action does not belong to this locked command.", nameof(action));
        staged.Add(action);
    }

    internal void Stage(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        staged.Add(auditEvent);
    }

    internal void StageInto(EePulseDbContext db, StagedCommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(outcome);
        var outcomeMatches = outcome.OutcomeKind switch
        {
            "general_comment" => staged.OfType<IncidentComment>().Any(x => x.Id == outcome.OutcomeId),
            "acknowledgement" or "manual_resolution" => staged.OfType<IncidentLifecycleAction>().Any(x => x.EventId == outcome.OutcomeId),
            _ => false,
        };
        if (!outcomeMatches)
            throw new InvalidOperationException("The staged outcome is not owned by this locked command.");
        foreach (var entity in staged) db.Add(entity);
    }
}

internal interface IIncidentEtagProvider
{
    string Create(Guid incidentId, long rowVersion);
}

internal sealed class IncidentEtagV1 : IIncidentEtagProvider
{
    internal const string Marker = "EE-PULSE/WP07/INCIDENT-ETAG/V1";
    private static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes(Marker);

    public string Create(Guid incidentId, long rowVersion) =>
        Wp07DashboardCanonicalizer.EtagFor(CreateCanonicalBytes(incidentId, rowVersion).AsSpan());

    internal static ImmutableArray<byte> CreateCanonicalBytes(Guid incidentId, long rowVersion)
    {
        if (incidentId == Guid.Empty) throw new ArgumentException("Incident ID is required.", nameof(incidentId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowVersion);

        var incidentIdBytes = Encoding.ASCII.GetBytes(incidentId.ToString("D"));
        var rowVersionBytes = Encoding.ASCII.GetBytes(rowVersion.ToString(CultureInfo.InvariantCulture));
        var totalLength = checked(MarkerBytes.Length + 1 + sizeof(uint) + incidentIdBytes.Length + sizeof(uint) + rowVersionBytes.Length);
        var canonicalBytes = new byte[totalLength];
        var offset = 0;
        MarkerBytes.CopyTo(canonicalBytes, offset);
        offset += MarkerBytes.Length;
        canonicalBytes[offset++] = 0;
        IncidentRequestFingerprintV1.WriteField(canonicalBytes, ref offset, incidentIdBytes);
        IncidentRequestFingerprintV1.WriteField(canonicalBytes, ref offset, rowVersionBytes);
        return ImmutableArray.CreateRange(canonicalBytes);
    }
}

internal static class IncidentRequestFingerprintV1
{
    internal const short Version = 1;
    internal const string Marker = "EE-PULSE/WP07/REQUEST-FINGERPRINT/V1";
    private static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes(Marker);
    private static readonly byte[] PostBytes = Encoding.ASCII.GetBytes("POST");

    internal static ImmutableArray<byte> Compute(
        short storedVersion,
        IncidentCommandRoute route,
        Guid incidentId,
        Guid actorId,
        string parsedStrongIfMatch,
        ReadOnlySpan<byte> canonicalRequestBodyUtf8)
    {
        if (storedVersion != Version)
            throw new NotSupportedException($"Unsupported incident request fingerprint version {storedVersion}.");
        if (!Enum.IsDefined(route)) throw new ArgumentOutOfRangeException(nameof(route));
        if (incidentId == Guid.Empty) throw new ArgumentException("Incident ID is required.", nameof(incidentId));
        if (actorId == Guid.Empty) throw new ArgumentException("Actor ID is required.", nameof(actorId));
        if (!IncidentConcurrencyContract.IsStrongOpaqueEtag(parsedStrongIfMatch))
            throw new ArgumentException("If-Match must be a strong opaque ETag.", nameof(parsedStrongIfMatch));
        if (canonicalRequestBodyUtf8.IsEmpty)
            throw new ArgumentException("Canonical request bytes are required.", nameof(canonicalRequestBodyUtf8));

        return ImmutableArray.CreateRange(SHA256.HashData(CreateCanonicalBytes(
            route, incidentId, actorId, parsedStrongIfMatch, canonicalRequestBodyUtf8).AsSpan()));
    }

    internal static ImmutableArray<byte> CreateCanonicalBytes(
        IncidentCommandRoute route,
        Guid incidentId,
        Guid actorId,
        string parsedStrongIfMatch,
        ReadOnlySpan<byte> canonicalRequestBodyUtf8)
    {
        if (!Enum.IsDefined(route)) throw new ArgumentOutOfRangeException(nameof(route));
        if (incidentId == Guid.Empty) throw new ArgumentException("Incident ID is required.", nameof(incidentId));
        if (actorId == Guid.Empty) throw new ArgumentException("Actor ID is required.", nameof(actorId));
        if (!IncidentConcurrencyContract.IsStrongOpaqueEtag(parsedStrongIfMatch))
            throw new ArgumentException("If-Match must be a strong opaque ETag.", nameof(parsedStrongIfMatch));
        if (canonicalRequestBodyUtf8.IsEmpty)
            throw new ArgumentException("Canonical request bytes are required.", nameof(canonicalRequestBodyUtf8));

        var routeBytes = Encoding.ASCII.GetBytes(RouteTemplate(route));
        var incidentIdBytes = Encoding.ASCII.GetBytes(incidentId.ToString("D"));
        var actorIdBytes = Encoding.ASCII.GetBytes(actorId.ToString("D"));
        var ifMatchBytes = StrongOpaqueEtagBytes(parsedStrongIfMatch);
        var bodyBytes = canonicalRequestBodyUtf8.ToArray();
        var totalLength = checked(MarkerBytes.Length + 1 +
            FieldStorageLength(PostBytes.Length) + FieldStorageLength(routeBytes.Length) +
            FieldStorageLength(incidentIdBytes.Length) + FieldStorageLength(actorIdBytes.Length) +
            FieldStorageLength(ifMatchBytes.Length) + FieldStorageLength(bodyBytes.Length));
        var bytes = new byte[totalLength];
        var offset = 0;
        MarkerBytes.CopyTo(bytes, offset);
        offset += MarkerBytes.Length;
        bytes[offset++] = 0;
        WriteField(bytes, ref offset, PostBytes);
        WriteField(bytes, ref offset, routeBytes);
        WriteField(bytes, ref offset, incidentIdBytes);
        WriteField(bytes, ref offset, actorIdBytes);
        WriteField(bytes, ref offset, ifMatchBytes);
        WriteField(bytes, ref offset, bodyBytes);
        return ImmutableArray.CreateRange(bytes);
    }

    internal static bool Matches(
        short storedVersion,
        ReadOnlySpan<byte> storedDigest,
        IncidentCommandRoute route,
        Guid incidentId,
        Guid actorId,
        string parsedStrongIfMatch,
        ReadOnlySpan<byte> canonicalRequestBodyUtf8)
    {
        if (storedDigest.Length != 32) return false;
        var calculated = Compute(storedVersion, route, incidentId, actorId, parsedStrongIfMatch, canonicalRequestBodyUtf8);
        return CryptographicOperations.FixedTimeEquals(storedDigest, calculated.AsSpan());
    }

    internal static uint CheckedU32Length(long byteLength) => checked((uint)byteLength);

    internal static void WriteField(Span<byte> destination, ref int offset, ReadOnlySpan<byte> field)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], CheckedU32Length(field.Length));
        offset += sizeof(uint);
        field.CopyTo(destination[offset..]);
        offset += field.Length;
    }

    private static int FieldStorageLength(int byteLength) => checked(sizeof(uint) + checked((int)CheckedU32Length(byteLength)));

    private static byte[] StrongOpaqueEtagBytes(string parsedStrongIfMatch)
    {
        if (!IncidentConcurrencyContract.IsStrongOpaqueEtag(parsedStrongIfMatch))
            throw new ArgumentException("If-Match must be a strong opaque ETag.", nameof(parsedStrongIfMatch));

        var bytes = new byte[parsedStrongIfMatch.Length];
        for (var index = 0; index < parsedStrongIfMatch.Length; index++)
            bytes[index] = checked((byte)parsedStrongIfMatch[index]);
        return bytes;
    }

    internal static string RouteTemplate(IncidentCommandRoute route) => route switch
    {
        IncidentCommandRoute.Acknowledge => "/api/v1/incidents/{id}/acknowledge",
        IncidentCommandRoute.AddComment => "/api/v1/incidents/{id}/comments",
        IncidentCommandRoute.Resolve => "/api/v1/incidents/{id}/resolve",
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };
}

internal static class IncidentIdempotencyAdvisoryKeyDeriver
{
    internal const int NamespaceKey = 1464873015;
    internal const string Marker = "EE-PULSE/WP07/IDEMPOTENCY";
    private static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes(Marker);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static (int NamespaceKey, int DerivedKey) Derive(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        var keyBytes = Utf8.GetBytes(idempotencyKey);
        var input = new byte[checked(MarkerBytes.Length + 1 + keyBytes.Length)];
        MarkerBytes.CopyTo(input, 0);
        input[MarkerBytes.Length] = 0;
        keyBytes.CopyTo(input, MarkerBytes.Length + 1);
        var digest = SHA256.HashData(input);
        var bits = ((uint)digest[0] << 24) |
                   ((uint)digest[1] << 16) |
                   ((uint)digest[2] << 8) |
                   digest[3];
        return (NamespaceKey, unchecked((int)bits));
    }
}

/// <summary>
/// Immutable, startup-owned cursor signing keys. Configuration is deliberately parsed once
/// and rejected as a whole: cursors never have an ambient or generated fallback key.
/// </summary>
internal interface IIncidentCursorKeyRing
{
    string ActiveKeyId { get; }
    ImmutableArray<byte> ActiveKey { get; }
    bool TryGetKey(string keyId, out ImmutableArray<byte> key);
}

internal sealed record IncidentCursorBinding(string Route, string? DeviceId, IncidentListFilter? Filter, int SchemaVersion, string? Sort = null);
internal sealed record IncidentSeek(DateTimeOffset OpenedAt, Guid IncidentId, int? SourceRank = null);
internal enum IncidentCursorValidation { Valid, Invalid, FilterMismatch }

internal sealed class IncidentCursorProtector(IIncidentCursorKeyRing ring, IUtcClock clock)
{
    private sealed record Envelope(int Version, string Kid, long IssuedAtMs, IncidentCursorBinding Binding, IncidentSeek Seek);
    internal const long LifetimeMilliseconds = 86_400_000;

    internal string Issue(IncidentCursorBinding binding, IncidentSeek seek)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, ring.ActiveKeyId,
            clock.UtcNow.ToUnixTimeMilliseconds(), binding, seek));
        var mac = HMACSHA256.HashData(ring.ActiveKey.AsSpan(), payload);
        return Encode(payload.Concat(mac).ToArray());
    }

    internal IncidentCursorValidation Validate(string cursor, IncidentCursorBinding binding, out IncidentSeek? seek)
    {
        seek = null;
        try
        {
            if (cursor.Length is 0 or > Wp07DashboardContract.MaximumCursorLength ||
                cursor.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                return IncidentCursorValidation.Invalid;
            var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4));
            if (bytes.Length <= 32 || !string.Equals(Encode(bytes), cursor, StringComparison.Ordinal)) return IncidentCursorValidation.Invalid;
            var payload = bytes.AsSpan(0, bytes.Length - 32);
            var envelope = JsonSerializer.Deserialize<Envelope>(payload);
            if (envelope is null || envelope.Version != 1 || envelope.Kid is null ||
                !ring.TryGetKey(envelope.Kid, out var key) ||
                !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key.AsSpan(), payload), bytes.AsSpan(bytes.Length - 32)) ||
                !IsCurrent(envelope.IssuedAtMs, clock.UtcNow.ToUnixTimeMilliseconds()) ||
                envelope.Binding is null || envelope.Seek is null || envelope.Seek.IncidentId == Guid.Empty ||
                envelope.Seek.OpenedAt.Offset != TimeSpan.Zero || envelope.Seek.SourceRank is not null and not (0 or 1))
                return IncidentCursorValidation.Invalid;
            if (envelope.Binding != binding) return IncidentCursorValidation.FilterMismatch;
            seek = envelope.Seek;
            return IncidentCursorValidation.Valid;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or OverflowException)
        {
            return IncidentCursorValidation.Invalid;
        }
    }

    internal static bool IsCurrent(long issuedAtMs, long nowMs)
    {
        try { return nowMs >= issuedAtMs && nowMs < checked(issuedAtMs + LifetimeMilliseconds); }
        catch (OverflowException) { return false; }
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class IncidentCursorKeyRing : IIncidentCursorKeyRing
{
    internal const string InvalidConfigurationMessage = "Incident cursor key ring configuration is invalid.";
    private readonly ImmutableDictionary<string, ImmutableArray<byte>> keys;

    private IncidentCursorKeyRing(string activeKeyId, ImmutableDictionary<string, ImmutableArray<byte>> keys)
    {
        ActiveKeyId = activeKeyId;
        this.keys = keys;
        ActiveKey = keys[activeKeyId];
    }

    public string ActiveKeyId { get; }
    public ImmutableArray<byte> ActiveKey { get; }

    public bool TryGetKey(string keyId, out ImmutableArray<byte> key)
    {
        key = default;
        return keyId is not null && keys.TryGetValue(keyId, out key);
    }

    internal static IncidentCursorKeyRing Parse(string? activeKeyId, string? keyRing)
    {
        if (!IsValidKeyId(activeKeyId) || string.IsNullOrEmpty(keyRing) || keyRing.Any(char.IsWhiteSpace))
            throw InvalidConfiguration();

        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var entry in keyRing.Split(';', StringSplitOptions.None))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
                throw InvalidConfiguration();

            var keyId = entry[..separator];
            var encodedKey = entry[(separator + 1)..];
            if (!IsValidKeyId(keyId) || !TryDecodeKey(encodedKey, out var key) || !builder.TryAdd(keyId, ImmutableArray.CreateRange(key)))
                throw InvalidConfiguration();
        }

        if (activeKeyId is null || !builder.ContainsKey(activeKeyId)) throw InvalidConfiguration();
        return new IncidentCursorKeyRing(activeKeyId, builder.ToImmutable());
    }

    private static bool IsValidKeyId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !IsAsciiAlphaNumeric(value[0])) return false;
        return value.All(character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool TryDecodeKey(string value, out byte[] key)
    {
        key = [];
        if (value.Length != 44 || value[^1] != '=' || value[..^1].Any(character =>
                !IsAsciiAlphaNumeric(character) && character is not '+' and not '/'))
            return false;

        try
        {
            key = Convert.FromBase64String(value);
            return key.Length == 32 && string.Equals(Convert.ToBase64String(key), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static InvalidOperationException InvalidConfiguration() => new(InvalidConfigurationMessage);
}

internal static class IncidentRuntimeBoundaryValidation
{
    internal static void RequireNonEmptyGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty) throw new ArgumentException("A non-empty identifier is required.", parameterName);
    }

    internal static void RequireStrongEtag(string value, string parameterName)
    {
        if (!IncidentConcurrencyContract.IsStrongOpaqueEtag(value))
            throw new ArgumentException("ETag must be strong and opaque.", parameterName);
    }

    internal static void RequireSafeCorrelationId(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "N", out var parsed) || parsed == Guid.Empty ||
            !string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal))
            throw new ArgumentException("Correlation ID must be a lowercase UUID-N.", parameterName);
    }

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.", parameterName);
    }
}
