using System.Text.Json;
using System.Text.Json.Serialization;
using EePulse.Api.Authorization;
using EePulse.Application.Time;
using EePulse.Contracts.Dashboard;
using EePulse.Domain.Auditing;
using EePulse.Domain.Preferences;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Npgsql;

namespace EePulse.Api.Timezone;

public static class TimezonePreferenceEndpoints
{
    internal const string SafeCorrelationIdItemKey = "WP07.TimezonePreference.SafeCorrelationId";
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static IEndpointRouteBuilder MapTimezonePreferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var route = endpoints.MapGroup(Wp07DashboardContract.TimezonePreferencePath)
            .WithTags("Dashboard - Timezone preference")
            .RequireAuthorization(EePulse.Api.Authorization.DashboardAuthorization.DashboardReadPolicy);

        route.MapGet("", Get)
            .WithName("GetTimezonePreference")
            .WithSummary("Get the caller's persisted timezone preference")
            .WithDescription("Returns the persisted canonical IANA timezone, or null when no preference is stored. The response is cacheable with the strong ETag header.")
            .Produces<TimezonePreferenceResponse>()
            .Produces(304)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403);
        route.MapPut("", Put)
            .WithName("PutTimezonePreference")
            .WithSummary("Set or clear the caller's persisted timezone preference")
            .WithDescription("Stores a canonical IANA timezone or clears the retained preference row. If-Match is required for optimistic concurrency; identical values are idempotent no-ops.")
            .Accepts<TimezonePreferenceRequest>("application/json")
            .Produces<TimezonePreferenceResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(412)
            .ProducesProblem(428)
            .ProducesProblem(500);
        return endpoints;
    }

    /// <summary>
    /// Replaces a caller-provided correlation value for this route before authentication and
    /// authorization may short-circuit.  It deliberately does not affect other API routes.
    /// </summary>
    public static IApplicationBuilder UseTimezonePreferenceCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Wp07DashboardContract.TimezonePreferencePath))
            {
                var correlationId = Guid.NewGuid().ToString("N");
                context.Items[SafeCorrelationIdItemKey] = correlationId;
                context.TraceIdentifier = correlationId;
                context.Response.Headers["X-Correlation-ID"] = correlationId;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-Correlation-ID"] = correlationId;
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });

    private static async Task<IResult> Get(
        HttpContext context,
        [FromServices] PrincipalIdentityResolver identities,
        [FromServices] TimezonePreferenceStore store,
        CancellationToken cancellationToken)
    {
        var correlationId = NewSafeCorrelationId(context);
        if (!identities.TryResolve(context.User, out var identity)) return Problem(context, correlationId, StatusCodes.Status401Unauthorized, "timezone-preference-identity-required", "A complete preference identity is required.");

        var current = await store.ReadAsync(identity!, cancellationToken);
        var ifNoneMatch = TimezonePreferenceContract.ClassifyIfNoneMatch(context.Request.Headers.IfNoneMatch, current.Etag, current.RowExists);
        if (ifNoneMatch == TimezoneIfNoneMatchClassification.Invalid)
            return Problem(context, correlationId, StatusCodes.Status400BadRequest, "invalid-if-none-match", "The If-None-Match value is invalid.");

        context.Response.Headers.ETag = current.Etag;
        if (ifNoneMatch == TimezoneIfNoneMatchClassification.Match)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return Results.Ok(new TimezonePreferenceResponse(current.Timezone, current.Etag));
    }

    private static async Task<IResult> Put(
        HttpContext context,
        [FromServices] PrincipalIdentityResolver identities,
        [FromServices] TimezonePreferenceStore store,
        CancellationToken cancellationToken)
    {
        var correlationId = NewSafeCorrelationId(context);
        if (!identities.TryResolve(context.User, out var identity)) return Problem(context, correlationId, StatusCodes.Status401Unauthorized, "timezone-preference-identity-required", "A complete preference identity is required.");

        _ = TimezonePreferenceContract.TryGetIfMatchVersion(context.Request.Headers.IfMatch, out var expectedVersion, out var ifMatch);
        if (ifMatch == TimezoneIfMatchClassification.Missing)
            return Problem(context, correlationId, StatusCodes.Status428PreconditionRequired, "missing-if-match", "If-Match is required.");
        if (ifMatch == TimezoneIfMatchClassification.Invalid)
            return Problem(context, correlationId, StatusCodes.Status400BadRequest, TimezonePreferenceContract.InvalidIfMatchCode, "The If-Match value is invalid.");

        TimezonePreferenceRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TimezonePreferenceRequest>(context.Request.Body, RequestJson, cancellationToken);
        }
        catch (JsonException)
        {
            return Problem(context, correlationId, StatusCodes.Status400BadRequest, "invalid-json", "The request body is invalid.");
        }

        if (request is null || !TimezonePreferenceContract.TryNormalizeTimezone(request.Timezone, out var canonicalTimezone))
            return Problem(context, correlationId, StatusCodes.Status400BadRequest, "invalid-timezone", "The timezone value is invalid.");

        TimezonePreferencePutResult result;
        try
        {
            result = await store.PutAsync(identity!, canonicalTimezone, correlationId, expectedVersion, cancellationToken);
        }
        catch (TimezonePreferenceWriteException)
        {
            return Problem(context, correlationId, StatusCodes.Status500InternalServerError, "timezone-preference-write-failed", "The timezone preference could not be saved.");
        }
        context.Response.Headers.ETag = result.Current.Etag;
        if (result.ConcurrencyConflict)
            return Problem(context, correlationId, StatusCodes.Status412PreconditionFailed, TimezonePreferenceContract.ConcurrencyConflictCode,
                "The timezone preference changed since it was read.", result.Current.Etag);

        return Results.Ok(new TimezonePreferenceResponse(result.Current.Timezone, result.Current.Etag));
    }

    private static string NewSafeCorrelationId(HttpContext context)
    {
        if (context.Items.TryGetValue(SafeCorrelationIdItemKey, out var value) && value is string existingCorrelationId)
        {
            context.Response.Headers["X-Correlation-ID"] = existingCorrelationId;
            return existingCorrelationId;
        }

        var generatedCorrelationId = Guid.NewGuid().ToString("N");
        context.Items[SafeCorrelationIdItemKey] = generatedCorrelationId;
        context.Response.Headers["X-Correlation-ID"] = generatedCorrelationId;
        return generatedCorrelationId;
    }

    private static IResult Problem(HttpContext context, string correlationId, int statusCode, string code, string detail, string? currentEtag = null)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = correlationId
        };
        if (currentEtag is not null) extensions["currentEtag"] = currentEtag;
        return Results.Problem(detail, statusCode: statusCode, title: code, extensions: extensions);
    }

}

internal static class TimezoneOpenApiMetadata
{
    public static OpenApiOperation DescribeGet(OpenApiOperation operation)
    {
        AddHeaderParameter(operation, "If-None-Match", "A comma-separated list of entity tags. Strong and weak tags use weak comparison; '*' is valid only as the sole member.");
        AddResponseHeaders(operation, includeEtag: true);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-if-none-match",
            ["401"] = "timezone-preference-identity-required"
        });
        return operation;
    }

    public static OpenApiOperation DescribePut(OpenApiOperation operation)
    {
        AddHeaderParameter(operation, "If-Match", "Exactly one canonical strong timezone ETag is required. Missing is 428; malformed, weak, wildcard, or multiple values are 400.", required: true);
        AddResponseHeaders(operation, includeEtag: true);
        DescribeProblems(operation, new Dictionary<string, string>
        {
            ["400"] = "invalid-json, invalid-timezone, invalid-if-match",
            ["401"] = "timezone-preference-identity-required",
            ["500"] = "timezone-preference-write-failed"
        });
        if (operation.Responses is null) return operation;
        if (operation.Responses.TryGetValue("412", out var conflict) && conflict is OpenApiResponse conflictResponse)
            conflictResponse.Description = "Problem Details code concurrency-conflict with currentEtag and correlationId extensions.";
        if (operation.Responses.TryGetValue("428", out var missing) && missing is OpenApiResponse missingResponse)
            missingResponse.Description = "Problem Details code missing-if-match with correlationId extension.";
        return operation;
    }

    private static void AddHeaderParameter(OpenApiOperation operation, string name, string description, bool required = false)
    {
        operation.Parameters ??= [];
        if (operation.Parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal) && parameter.In == ParameterLocation.Header)) return;
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }

    private static void AddResponseHeaders(OpenApiOperation operation, bool includeEtag)
    {
        if (operation.Responses is null) return;
        foreach (var (status, candidate) in operation.Responses)
        {
            if (candidate is not OpenApiResponse response) continue;
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["X-Correlation-ID"] = new OpenApiHeader
            {
                Description = "Server-generated lowercase UUID-N correlation identifier.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-9a-f]{32}$" }
            };
            if (includeEtag && status is "200" or "304" or "412")
            {
                response.Headers["ETag"] = new OpenApiHeader
                {
                    Description = "Strong timezone preference ETag; \"tz-0\" denotes the absent row and persisted rows use \"tz-{version}\".",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = TimezonePreferenceContract.StrongEtagPattern }
                };
            }
        }
    }

    private static void DescribeProblems(OpenApiOperation operation, IReadOnlyDictionary<string, string> codesByStatus)
    {
        if (operation.Responses is null) return;
        foreach (var (status, codes) in codesByStatus)
        {
            if (operation.Responses.TryGetValue(status, out var response) && response is OpenApiResponse problem)
                problem.Description = $"Problem Details with stable code ({codes}) and correlationId extension.";
        }
    }
}

public sealed class TimezoneOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        if (document.Paths?.TryGetValue(Wp07DashboardContract.TimezonePreferencePath, out var pathItem) == true && pathItem.Operations is not null)
        {
            if (pathItem.Operations.TryGetValue(HttpMethod.Get, out var get)) TimezoneOpenApiMetadata.DescribeGet(get);
            if (pathItem.Operations.TryGetValue(HttpMethod.Put, out var put)) TimezoneOpenApiMetadata.DescribePut(put);
        }

        return Task.CompletedTask;
    }
}

internal sealed class TimezonePreferenceStore(EePulseDbContext db, IUtcClock clock)
{
    public async Task<TimezonePreferenceRead> ReadAsync(PrincipalIdentity identity, CancellationToken cancellationToken)
    {
        var preference = await db.UserTimezonePreferences.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Issuer == identity.Issuer && candidate.Subject == identity.Subject, cancellationToken);
        return preference is null
            ? TimezonePreferenceRead.Absent
            : new(true, preference.Timezone, preference.Version);
    }

    public async Task<TimezonePreferencePutResult> PutAsync(
        PrincipalIdentity identity,
        string? canonicalTimezone,
        string correlationId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        Exception? primaryFailure = null;
        var preserveExpectedOutcome = false;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var preference = await db.UserTimezonePreferences.SingleOrDefaultAsync(
                candidate => candidate.Issuer == identity.Issuer && candidate.Subject == identity.Subject, cancellationToken);
            var current = preference is null ? TimezonePreferenceRead.Absent : new(true, preference.Timezone, preference.Version);
            if (current.Version != expectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(current, true);
            }

            if (preference is null)
            {
                if (canonicalTimezone is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new(current, false);
                }

                var now = clock.UtcNow;
                preference = new UserTimezonePreference(identity.Issuer, identity.Subject, canonicalTimezone, now);
                db.UserTimezonePreferences.Add(preference);
                AddAudit("create", preference.Version, correlationId, now);
            }
            else
            {
                var now = clock.UtcNow;
                if (!preference.ApplyTimezone(canonicalTimezone, now))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new(new(true, preference.Timezone, preference.Version), false);
                }
                AddAudit(canonicalTimezone is null ? "clear" : "update", preference.Version, correlationId, now);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(new(true, preference.Timezone, preference.Version), false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            primaryFailure = exception;
            preserveExpectedOutcome = true;
            await RollbackAndClearAsync(transaction, primaryFailure, preserveExpectedOutcome);
            return await ReloadConflictAsync(identity, exception, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsPreferenceUniqueConflict(exception))
        {
            primaryFailure = exception;
            preserveExpectedOutcome = true;
            await RollbackAndClearAsync(transaction, primaryFailure, preserveExpectedOutcome);
            return await ReloadConflictAsync(identity, exception, cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            primaryFailure = exception;
            await RollbackAndClearAsync(transaction, primaryFailure, preserveExpectedOutcome: false);
            throw;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            await RollbackAndClearAsync(transaction, primaryFailure, preserveExpectedOutcome: false);
            throw new TimezonePreferenceWriteException(exception);
        }
        finally
        {
            await DisposeTransactionAsync(transaction, primaryFailure, preserveExpectedOutcome);
        }
    }

    private async Task RollbackAndClearAsync(IDbContextTransaction? transaction, Exception? primaryFailure, bool preserveExpectedOutcome)
    {
        Exception? cleanupFailure = null;
        try
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException exception) when (IsCompletedTransaction(exception))
        {
            // PostgreSQL has already completed the transaction after a commit-time failure.
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            db.ChangeTracker.Clear();
        }
        catch (Exception exception)
        {
            if (cleanupFailure is null) cleanupFailure = exception;
            else cleanupFailure.Data["timezonePreferenceTrackerCleanupFailure"] = exception;
        }

        if (cleanupFailure is null) return;
        if (primaryFailure is not null)
        {
            primaryFailure.Data["timezonePreferenceRollbackCleanupFailure"] = cleanupFailure;
            if (preserveExpectedOutcome)
                throw new TimezonePreferenceWriteException(cleanupFailure);
            return;
        }

        throw new TimezonePreferenceWriteException(cleanupFailure);
    }

    private static async Task DisposeTransactionAsync(IDbContextTransaction? transaction, Exception? primaryFailure, bool preserveExpectedOutcome)
    {
        if (transaction is null) return;
        try
        {
            await transaction.DisposeAsync();
        }
        catch (InvalidOperationException exception) when (IsCompletedTransaction(exception))
        {
            // A failed commit can complete the underlying Npgsql transaction before disposal.
        }
        catch (Exception exception)
        {
            if (primaryFailure is not null)
            {
                primaryFailure.Data["timezonePreferenceTransactionDisposeCleanupFailure"] = exception;
                if (preserveExpectedOutcome)
                    throw new TimezonePreferenceWriteException(exception);
                return;
            }

            throw new TimezonePreferenceWriteException(exception);
        }
    }

    private static bool IsCompletedTransaction(InvalidOperationException exception) =>
        exception.Message.Contains("transaction has completed", StringComparison.OrdinalIgnoreCase);

    private async Task<TimezonePreferencePutResult> ReloadConflictAsync(
        PrincipalIdentity identity,
        Exception concurrencyException,
        CancellationToken cancellationToken)
    {
        try
        {
            db.ChangeTracker.Clear();
            return new(await ReadAsync(identity, cancellationToken), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            concurrencyException.Data["timezonePreferenceConflictReloadFailure"] = exception;
            throw new TimezonePreferenceWriteException(exception);
        }
    }

    private void AddAudit(string operation, long version, string correlationId, DateTimeOffset occurredAt)
    {
        db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(), null, "dashboard.timezone-preference.changed", "TimezonePreference", null, null,
            JsonSerializer.Serialize(new { operation, version }), correlationId, occurredAt, null));
    }

    private static bool IsPreferenceUniqueConflict(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "PK_user_timezone_preferences"
                })
            {
                return true;
            }
        }
        return false;
    }
}

internal sealed class TimezonePreferenceWriteException(Exception innerException) : Exception("Timezone preference write failed.", innerException);

internal sealed record TimezonePreferenceRead(bool RowExists, string? Timezone, long Version)
{
    public static TimezonePreferenceRead Absent { get; } = new(false, null, 0);
    public string Etag => TimezonePreferenceContract.EtagForVersion(Version);
}

internal sealed record TimezonePreferencePutResult(TimezonePreferenceRead Current, bool ConcurrencyConflict);
