using EePulse.Api.Authorization;
using EePulse.Api.Dashboard;
using EePulse.Domain.Auditing;
using EePulse.Domain.Identity;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using System.Data.Common;
using System.Globalization;

namespace EePulse.IntegrationTests;

public sealed class Wp07IncidentRuntimeApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingAuthoritativeIncidentReturnsNotFoundAndRollsBackGeneratedPrincipal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString);
            builder.UseEnvironment("Development");
        });

        var identity = new PrincipalIdentity("https://issuer.example", "operator-1");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>();
            var input = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, Guid.NewGuid(),
                "01234567-89ab-cdef-0123-456789abcdef", identity, "\"strong\"", "{}"u8,
                "0123456789abcdef0123456789abcdef", "comment");

            var result = await coordinator.ExecuteAsync(input, new ThrowingHandler(), ct);
            Assert.IsType<IncidentCommandResult<string>.NotFound>(result);
        }

        await using var verification = factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.False(await db.HumanPrincipals.AsNoTracking().AnyAsync(x =>
            x.Issuer == identity.Issuer && x.Subject == identity.Subject, ct));
    }

    [Fact]
    public async Task FreshSuccessUsesLockOrderAndReceiptLastTwoFlushes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString, null, observer);
        var seed = await SeedAsync(factory, ct);
        observer.Clear();
        var etag = new IncidentEtagV1().Create(seed.IncidentId, 1);
        var input = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, seed.IncidentId,
            "01234567-89ab-cdef-0123-456789abcdef", seed.Identity, etag, "{\"Comment\":\"x\"}"u8,
            "0123456789abcdef0123456789abcdef", "x");
        var handler = new CommentHandler();

        StoredIncidentHttpResponse fresh;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>()
                .ExecuteAsync(input, handler, ct);
            fresh = Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(result).Response;
        }

        Assert.Equal(1, handler.ApplyCount);
        Assert.Equal(1, handler.ResponseCount);
        await using (var verification = factory.Services.CreateAsyncScope())
        {
            var db = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var receipt = await db.IdempotencyReceipts.AsNoTracking().SingleAsync(x => x.IdempotencyKey == input.IdempotencyKey, ct);
            Assert.Equal(fresh.StatusCode, receipt.ResponseStatus);
            Assert.Equal(fresh.BodyUtf8.ToArray(), receipt.ResponseBody);
            Assert.Equal(fresh.ContentType, receipt.ResponseContentType);
            Assert.Equal(fresh.Etag, receipt.ResponseEtag);
            var principal = await db.HumanPrincipals.AsNoTracking().SingleAsync(ct);
            Assert.Equal(principal.Id, receipt.ActorId);
            Assert.Equal(1, await db.IncidentComments.CountAsync(ct));
            Assert.Equal(1, await db.AuditEvents.CountAsync(ct));
        }

        AssertLockAndReceiptOrder(observer.Commands);
    }

    [Fact]
    public async Task ExactReplayReturnsStoredRepresentationWithoutHandlerOrMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, Key(1), new IncidentEtagV1().Create(seed.IncidentId, 1));
        var handler = new CommentHandler();
        StoredIncidentHttpResponse fresh;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>().ExecuteAsync(input, handler, ct);
            fresh = Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(result).Response;
        }
        await using (var replayScope = factory.Services.CreateAsyncScope())
        {
            var replay = await replayScope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>()
                .ExecuteAsync(input, handler, ct);
            var replayResponse = Assert.IsType<IncidentCommandResult<string>.Replay>(replay).Response;
            Assert.Equal(fresh.StatusCode, replayResponse.StatusCode);
            Assert.Equal(fresh.BodyUtf8.ToArray(), replayResponse.BodyUtf8.ToArray());
            Assert.Equal(fresh.ContentType, replayResponse.ContentType);
            Assert.Equal(fresh.Etag, replayResponse.Etag);
        }
        Assert.Equal(1, handler.ApplyCount);
        Assert.Equal(1, handler.ResponseCount);
    }

    [Fact]
    public async Task SameKeyDifferentFingerprintReturnsReuseConflictWithoutMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var etag = new IncidentEtagV1().Create(seed.IncidentId, 1);
        var input = CreateInput(seed, Key(2), etag);
        await ExecuteAsync(factory, input, new CommentHandler(), ct);
        var conflicting = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, seed.IncidentId, Key(2),
            seed.Identity, etag, "{\"Comment\":\"different\"}"u8, "0123456789abcdef0123456789abcdef", "different");
        var handler = new ThrowingHandler();
        var result = await ExecuteAsync(factory, conflicting, handler, ct);
        Assert.IsType<IncidentCommandResult<string>.IdempotencyReuseConflict>(result);
        await AssertCountsAsync(factory, 1, 1, 1, ct);
    }

    [Fact]
    public async Task UnsupportedStoredFingerprintVersionReturnsDependencyFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, Key(3), new IncidentEtagV1().Create(seed.IncidentId, 1));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            var actorId = Guid.NewGuid();
            var commentId = Guid.NewGuid();
            var storedAt = Now.AddMinutes(1);
            var digest = new byte[32];
            var responseBody = "{\"stored\":true}"u8.ToArray();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL DEFERRED", ct);

            // Fixture-only: production accepts only v1. This disposable database is destroyed
            // after the test, so retained unsupported-version replay dispatch can be exercised.
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE idempotency_receipts DROP CONSTRAINT ck_idempotency_receipts_request_fingerprint_version", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO human_principals (id, issuer, subject, created_at) VALUES ({actorId}, {seed.Identity.Issuer}, {seed.Identity.Subject}, {storedAt})", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO incident_comments (id, incident_id, probe_id, author_id, comment, created_at, idempotency_key) VALUES ({commentId}, {seed.IncidentId}, {seed.ProbeId}, {actorId}, {"x"}, {storedAt}, {input.IdempotencyKey})", ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO idempotency_receipts (idempotency_key, route_template, incident_id, actor_id, request_fingerprint_version, request_digest, response_status, response_body, response_content_type, response_etag, outcome_kind, incident_comment_id, incident_lifecycle_action_id, created_at, completed_at) VALUES ({input.IdempotencyKey}, {"/api/v1/incidents/{id}/comments"}, {seed.IncidentId}, {actorId}, {2}, {digest}, {201}, {responseBody}, {"application/json; charset=utf-8"}, {"\"stored\""}, {"general_comment"}, {commentId}, {null}, {storedAt}, {storedAt})", ct);
            await transaction.CommitAsync(ct);
        }
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(await ExecuteAsync(factory, input, new ThrowingHandler(), ct));
        await AssertCountsAsync(factory, 1, 0, 1, ct);

        var differentActor = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, seed.IncidentId,
            input.IdempotencyKey, new PrincipalIdentity("https://issuer.example", "operator-2"), input.ParsedStrongIfMatch,
            input.CanonicalRequestBodyUtf8.AsSpan(), input.TrustedCorrelationId, input.Command);
        Assert.IsType<IncidentCommandResult<string>.IdempotencyReuseConflict>(
            await ExecuteAsync(factory, differentActor, new ThrowingHandler(), ct));
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(ct);
        await using var trigger = new NpgsqlCommand("SELECT COUNT(*) FROM pg_trigger WHERE tgname = 'tr_idempotency_receipts_immutable' AND tgenabled = 'O'", connection);
        Assert.Equal(1L, (long)(await trigger.ExecuteScalarAsync(ct))!);
        await using var function = new NpgsqlCommand("SELECT COUNT(*) FROM pg_proc WHERE proname = 'fn_wp07_incident_runtime_reject_mutation'", connection);
        Assert.Equal(1L, (long)(await function.ExecuteScalarAsync(ct))!);
    }

    [Fact]
    public async Task MissingReceiptPrincipalMappingReturnsDependencyFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, Key(4), new IncidentEtagV1().Create(seed.IncidentId, 1));
        await ExecuteAsync(factory, input, new CommentHandler(), ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE idempotency_receipts DISABLE TRIGGER ALL", ct);
            try { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE idempotency_receipts SET actor_id = {Guid.NewGuid()} WHERE idempotency_key = {input.IdempotencyKey}", ct); }
            finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE idempotency_receipts ENABLE TRIGGER ALL", ct); }
        }
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(await ExecuteAsync(factory, input, new ThrowingHandler(), ct));
    }

    [Fact]
    public async Task MissingProjectionReturnsDependencyFailureWithoutSurvivingPrincipal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_status_projections WHERE probe_id = {seed.ProbeId}", ct);
        }
        var input = CreateInput(seed, Key(5), new IncidentEtagV1().Create(seed.IncidentId, 1));
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(await ExecuteAsync(factory, input, new ThrowingHandler(), ct));
        await AssertCountsAsync(factory, 0, 0, 0, ct);
    }

    [Fact]
    public async Task StaleEtagWinsBeforeHandlerAndStateConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var result = await ExecuteAsync(factory, CreateInput(seed, Key(6), "\"stale\""), new ThrowingHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.FreshPreconditionFailed>(result);
        await AssertCountsAsync(factory, 0, 0, 0, ct);
    }

    [Fact]
    public async Task CurrentEtagStateConflictRollsBackStagedWork()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var result = await ExecuteAsync(factory, CreateInput(seed, Key(7), new IncidentEtagV1().Create(seed.IncidentId, 1)), new StateConflictHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.FreshStateConflict>(result);
        await AssertCountsAsync(factory, 0, 0, 0, ct);
    }

    [Fact]
    public async Task ExistingPrincipalIsRereadWithoutChangingImmutableMapping()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var existingId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
            db.HumanPrincipals.Add(new HumanPrincipal(existingId, seed.Identity.Issuer, seed.Identity.Subject, Now));
            await db.SaveChangesAsync(ct);
        }
        var input = CreateInput(seed, Key(8), new IncidentEtagV1().Create(seed.IncidentId, 1));
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await ExecuteAsync(factory, input, new CommentHandler(), ct));
        await using var verification = factory.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Equal(existingId, (await verificationDb.IdempotencyReceipts.AsNoTracking().SingleAsync(ct)).ActorId);
        Assert.Equal(1, await verificationDb.HumanPrincipals.CountAsync(ct));
    }

    [Fact]
    public async Task ConcurrentSameKeyExecutesOnceAndLoserReplays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, Key(9), new IncidentEtagV1().Create(seed.IncidentId, 1));
        var winner = new BlockingCommentHandler();
        var first = ExecuteAsync(factory, input, winner, ct);
        await winner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var second = ExecuteAsync(factory, input, new CommentHandler(), ct);
        await Task.Delay(150, ct);
        Assert.False(second.IsCompleted);
        winner.Release.TrySetResult();
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await first);
        Assert.IsType<IncidentCommandResult<string>.Replay>(await second);
        await AssertCountsAsync(factory, 1, 1, 1, ct);
    }

    [Fact]
    public async Task ForcedAdvisoryCollisionSerializesButKeepsFullKeysDistinct()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<IIncidentIdempotencyAdvisoryKeyProvider>();
            services.AddSingleton<IIncidentIdempotencyAdvisoryKeyProvider, CollidingAdvisoryKeyProvider>();
        });
        var firstSeed = await SeedAsync(factory, ct);
        var secondSeed = await SeedAsync(factory, ct);
        var firstEtag = new IncidentEtagV1().Create(firstSeed.IncidentId, 1);
        var secondEtag = new IncidentEtagV1().Create(secondSeed.IncidentId, 1);
        var winner = new BlockingCommentHandler();
        var loser = new CommentHandler();
        var first = ExecuteAsync(factory, CreateInput(firstSeed, Key(10), firstEtag), winner, ct);
        await winner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var second = ExecuteAsync(factory, CreateInput(secondSeed, Key(11), secondEtag), loser, ct);
        await WaitForAdvisoryWaitAsync(postgres.ConnectionString, IncidentIdempotencyAdvisoryKeyDeriver.NamespaceKey, 7, ct);
        Assert.Equal(0, loser.ApplyCount);
        winner.Release.TrySetResult();
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await first);
        Assert.IsType<IncidentCommandResult<string>.FreshSuccess>(await second);
        await AssertCountsAsync(factory, 2, 2, 2, ct);
        await using var verification = factory.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Equal(2, await verificationDb.IdempotencyReceipts.AsNoTracking()
            .CountAsync(receipt => receipt.IdempotencyKey == Key(10) || receipt.IdempotencyKey == Key(11), ct));
    }

    [Fact]
    public async Task FailureAfterFirstFlushRollsBackPrincipalChildAuditAndReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var factory = CreateFactory(postgres.ConnectionString);
        var seed = await SeedAsync(factory, ct);
        var before = await SnapshotAsync(factory, ct);
        var result = await ExecuteAsync(factory, CreateInput(seed, Key(12), new IncidentEtagV1().Create(seed.IncidentId, 1)), new ResponseFailureHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(result);
        await AssertCountsAsync(factory, 0, 0, 0, ct);
        Assert.Equal(before, await SnapshotAsync(factory, ct));
        await AssertTransactionBoundaryClassificationAsync(ct);
    }

    [Fact]
    public async Task ServerRejectedCommitIsDeterminateAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var observer = new RuntimeCommandObserver();
        await using var factory = CreateFactory(postgres.ConnectionString, null, observer);
        var seed = await SeedAsync(factory, ct);
        var before = await SnapshotAsync(factory, ct);
        await InstallDeferredCommitRejectionTriggerAsync(factory, ct);
        var result = await ExecuteAsync(factory, CreateInput(seed, Key(13), new IncidentEtagV1().Create(seed.IncidentId, 1)), new CommentHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(result);
        await AssertCountsAsync(factory, 0, 0, 0, ct);
        Assert.Equal(before, await SnapshotAsync(factory, ct));
        Assert.Contains(observer.Commands, command => command.Contains("incident_comments", StringComparison.Ordinal) && command.Contains("INSERT", StringComparison.Ordinal));
        Assert.Contains(observer.Commands, command => command.Contains("idempotency_receipts", StringComparison.Ordinal) && command.Contains("INSERT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousCommitNeverReturnsSuccessAndRetryReplaysDurableReceipt()
    {
        await AssertIndeterminateCommitCaseAsync(false, Key(14));
        await AssertIndeterminateCommitCaseAsync(true, Key(15));
    }

    private static async Task AssertTransactionBoundaryClassificationAsync(CancellationToken ct)
    {
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var identity = new PrincipalIdentity("https://issuer.example", "operator-1");
        var missingInput = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, Guid.NewGuid(), Key(90), identity,
            "\"strong\"", "{}"u8, "0123456789abcdef0123456789abcdef", "x");

        await using (var beginFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.Begin))))
        {
            Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(
                await ExecuteAsync(beginFactory, missingInput, new ThrowingHandler(), ct));
        }

        await using (var rollbackFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.Rollback))))
        {
            var seed = await SeedAsync(rollbackFactory, ct);
            var before = await SnapshotAsync(rollbackFactory, ct);
            Assert.IsType<IncidentCommandResult<string>.DependencyFailure>(await ExecuteAsync(rollbackFactory,
                CreateInput(seed, Key(91), new IncidentEtagV1().Create(seed.IncidentId, 1)), new ResponseFailureHandler(), ct));
            Assert.Equal(before, await SnapshotAsync(rollbackFactory, ct));
        }

        await using (var disposalFactory = CreateFactory(postgres.ConnectionString, services => ReplaceTransactionBoundary(
            services, new FaultingTransactionBoundary(TransactionFault.Dispose))))
        {
            var seed = await SeedAsync(disposalFactory, ct);
            Assert.IsType<IncidentCommandResult<string>.FreshPreconditionFailed>(await ExecuteAsync(disposalFactory,
                CreateInput(seed, Key(92), "\"stale\""), new ThrowingHandler(), ct));
        }
    }

    private static async Task AssertIndeterminateCommitCaseAsync(bool cancellation, string idempotencyKey)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var cleanupToken = CancellationToken.None;
        var transactionBoundary = new RecordingTransactionBoundary();
        await using var factory = CreateFactory(postgres.ConnectionString, services =>
        {
            services.RemoveAll<IIncidentCommitBoundary>();
            services.AddSingleton<IIncidentCommitBoundary>(new CommitThenIndeterminateBoundary(cancellation));
            ReplaceTransactionBoundary(services, transactionBoundary);
        });
        var seed = await SeedAsync(factory, ct);
        var input = CreateInput(seed, idempotencyKey, new IncidentEtagV1().Create(seed.IncidentId, 1));
        var result = await ExecuteAsync(factory, input, new CommentHandler(), ct);
        Assert.IsType<IncidentCommandResult<string>.IndeterminateCommit>(result);
        Assert.Equal(0, transactionBoundary.RollbackCalls);

        await using var verification = factory.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var receipt = await verificationDb.IdempotencyReceipts.AsNoTracking()
            .SingleAsync(value => value.IdempotencyKey == idempotencyKey, cleanupToken);
        var replay = Assert.IsType<IncidentCommandResult<string>.Replay>(
            await ExecuteAsync(factory, input, new ThrowingHandler(), cleanupToken));
        Assert.Equal(receipt.ResponseStatus, replay.Response.StatusCode);
        Assert.Equal(receipt.ResponseBody, replay.Response.BodyUtf8.ToArray());
        Assert.Equal(receipt.ResponseContentType, replay.Response.ContentType);
        Assert.Equal(receipt.ResponseEtag, replay.Response.Etag);
        await AssertCountsAsync(factory, 1, 1, 1, cleanupToken);
    }

    private static async Task InstallDeferredCommitRejectionTriggerAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION public.wp07_test_reject_receipt_commit()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'wp07-test-deferred-commit-rejection' USING ERRCODE = 'P0001';
            END;
            $$;
            CREATE CONSTRAINT TRIGGER tr_wp07_test_reject_receipt_commit
            AFTER INSERT ON idempotency_receipts
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION public.wp07_test_reject_receipt_commit();
            """, ct);
    }

    private static async Task WaitForAdvisoryWaitAsync(string connectionString, int namespaceKey, int derivedKey, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT COUNT(*)
                FROM pg_locks locks
                JOIN pg_stat_activity activity ON activity.pid = locks.pid
                WHERE locks.locktype = 'advisory'
                  AND locks.classid = @namespace_key::oid
                  AND locks.objid = @derived_key::oid
                  AND NOT locks.granted
                  AND activity.wait_event_type = 'Lock'
                """, connection);
            command.Parameters.AddWithValue("namespace_key", namespaceKey);
            command.Parameters.AddWithValue("derived_key", derivedKey);
            if ((long)(await command.ExecuteScalarAsync(ct))! > 0) return;
            await Task.Delay(25, ct);
        }

        throw new Xunit.Sdk.XunitException("The losing request did not wait on the forced idempotency advisory lock.");
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        var incident = await db.AvailabilityIncidents.AsNoTracking().OrderBy(value => value.Id).Select(value => new
        {
            value.Id, value.ProbeId, value.Status, value.OpenedAt, value.AcknowledgedAt, value.AcknowledgedBy,
            value.AcknowledgementComment, value.ResolvedAt, value.ResolvedBy, value.ResolutionNote, value.OccurrenceCount, value.RowVersion
        }).ToListAsync(ct);
        var projection = await db.ProbeStatusProjections.AsNoTracking().OrderBy(value => value.ProbeId).Select(value => new
        {
            value.ProbeId, value.UnderlyingStatus, value.VisibleStatus, value.ConsecutiveFailureCount, value.ConsecutiveSuccessCount,
            value.OpenIncidentId, value.StateVersion, value.WatermarkAgentId, value.WatermarkResultId, value.WatermarkEventAt,
            value.LastFreshEventAt
        }).ToListAsync(ct);
        var principal = await db.HumanPrincipals.AsNoTracking().OrderBy(value => value.Id)
            .Select(value => new { value.Id, value.Issuer, value.Subject, value.CreatedAt }).ToListAsync(ct);
        var comments = await db.IncidentComments.AsNoTracking().OrderBy(value => value.Id).Select(value => new
        {
            value.Id, value.IncidentId, value.ProbeId, value.AuthorId, value.Comment, value.CreatedAt, value.IdempotencyKey
        }).ToListAsync(ct);
        var actions = await db.IncidentLifecycleActions.AsNoTracking().OrderBy(value => value.EventId).Select(value => new
        {
            value.EventId, value.IncidentId, value.ProbeId, value.ActorId, value.ActionKind, value.ReasonCode, value.OccurredAt, value.ActionNote, value.IdempotencyKey
        }).ToListAsync(ct);
        var audits = await db.AuditEvents.AsNoTracking().OrderBy(value => value.Id).Select(value => new
        {
            value.Id, value.ActorId, value.Action, value.EntityType, value.EntityId, value.CorrelationId, value.OccurredAt
        }).ToListAsync(ct);
        var receipts = await db.IdempotencyReceipts.AsNoTracking().OrderBy(value => value.IdempotencyKey).Select(value => new
        {
            value.IdempotencyKey, value.IncidentId, value.ActorId, value.RequestFingerprintVersion, value.RequestDigest,
            value.ResponseStatus, value.ResponseBody, value.ResponseContentType, value.ResponseEtag, value.OutcomeKind,
            value.IncidentCommentId, value.IncidentLifecycleActionId, value.CreatedAt, value.CompletedAt
        }).ToListAsync(ct);
        return System.Text.Json.JsonSerializer.Serialize(new { incident, projection, principal, comments, actions, audits, receipts });
    }

    private static void ReplaceTransactionBoundary(IServiceCollection services, IIncidentTransactionBoundary boundary)
    {
        services.RemoveAll<IIncidentTransactionBoundary>();
        services.AddSingleton(boundary);
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString,
        Action<IServiceCollection>? configure = null, params DbCommandInterceptor[] interceptors) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connectionString);
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                if (interceptors.Length > 0)
                    services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString).AddInterceptors(interceptors));
                configure?.Invoke(services);
            });
        });

    private static IncidentCommandInput<string> CreateInput((Guid IncidentId, Guid ProbeId, PrincipalIdentity Identity) seed,
        string idempotencyKey, string etag) =>
        new(IncidentCommandRoute.AddComment, seed.IncidentId, idempotencyKey, seed.Identity, etag,
            "{\"Comment\":\"x\"}"u8, "0123456789abcdef0123456789abcdef", "x");

    private static string Key(int value) => "00000000-0000-0000-0000-" + value.ToString("D12", CultureInfo.InvariantCulture);

    private static async Task<IncidentCommandResult<string>> ExecuteAsync(WebApplicationFactory<Program> factory,
        IncidentCommandInput<string> input, IIncidentLockedCommandHandler<string, string> handler, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIncidentCommandCoordinator>().ExecuteAsync(input, handler, ct);
    }

    private static async Task AssertCountsAsync(WebApplicationFactory<Program> factory, int comments, int audits, int receipts, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        Assert.Equal(comments, await db.IncidentComments.CountAsync(ct));
        Assert.Equal(audits, await db.AuditEvents.CountAsync(ct));
        Assert.Equal(receipts, await db.IdempotencyReceipts.CountAsync(ct));
    }

    private static void AssertLockAndReceiptOrder(IReadOnlyList<string> commands)
    {
        var list = commands.ToList();
        var idempotency = list.FindIndex(command => command.Contains("pg_advisory_xact_lock", StringComparison.Ordinal));
        var probe = list.FindIndex(command => command.Contains("hashtextextended", StringComparison.Ordinal));
        var projection = list.FindIndex(command => command.Contains("probe_status_projections", StringComparison.Ordinal) && command.Contains("FOR UPDATE", StringComparison.Ordinal));
        var incident = list.FindIndex(command => command.Contains("availability_incidents", StringComparison.Ordinal) && command.Contains("FOR UPDATE", StringComparison.Ordinal));
        var comment = list.FindIndex(command => command.Contains("incident_comments", StringComparison.Ordinal) && command.Contains("INSERT", StringComparison.Ordinal));
        var receipt = list.FindIndex(command => command.Contains("idempotency_receipts", StringComparison.Ordinal) && command.Contains("INSERT", StringComparison.Ordinal));
        Assert.True(idempotency >= 0 && probe > idempotency && projection > probe && incident > projection);
        Assert.True(comment >= 0 && receipt > comment);
    }

    private static async Task<(Guid IncidentId, Guid ProbeId, PrincipalIdentity Identity)> SeedAsync(WebApplicationFactory<Program> factory, CancellationToken ct)
    {
        var siteId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var probeId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var fixtureSuffix = Guid.NewGuid().ToString("N");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EePulseDbContext>();
        db.Add(new Site(siteId, "SITE-" + fixtureSuffix[..8], "Site " + fixtureSuffix, "UTC", Now));
        db.Add(new AgentGroup(groupId, "Group " + fixtureSuffix, null, Now));
        db.Add(new Device(deviceId, siteId, "Device " + fixtureSuffix, "192.0.2.10", null, "router", null, null, Criticality.Normal, [], Now));
        db.Add(new Probe(probeId, deviceId, groupId, 60, 1_000, 1, null, null, 1, 1));
        await db.SaveChangesAsync(ct);
        db.Add(new AvailabilityIncident(incidentId, probeId, Now));
        db.Add(new ProbeStatusProjection(probeId, ProbeStatus.Up, 0, 0, null, null, null, null));
        await db.SaveChangesAsync(ct);
        return (incidentId, probeId, new PrincipalIdentity("https://issuer.example", "operator-1"));
    }

    private sealed class CommentHandler : IIncidentLockedCommandHandler<string, string>
    {
        public int ApplyCount { get; private set; }
        public int ResponseCount { get; private set; }

        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            ApplyCount++;
            var comment = new IncidentComment(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId,
                context.ActorId, context.Command, context.OccurredAt, context.IdempotencyKey);
            context.Stage(comment);
            context.Stage(new AuditEvent(Guid.NewGuid(), context.ActorId, "incident.comment.add", "AvailabilityIncident",
                context.Incident.Id, null, null, context.TrustedCorrelationId, context.OccurredAt, null));
            return ValueTask.FromResult<LockedCommandDecision<string>>(new LockedCommandDecision<string>.Apply(
                new StagedCommandOutcome(comment.Id, "general_comment", context.OccurredAt)));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken)
        {
            ResponseCount++;
            return ValueTask.FromResult(new StoredIncidentHttpResponse(201, "{\"created\":true}"u8,
                "application/json; charset=utf-8", context.ResultingEtag));
        }
    }

    private sealed class BlockingCommentHandler : IIncidentLockedCommandHandler<string, string>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var comment = new IncidentComment(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId,
                context.ActorId, context.Command, context.OccurredAt, context.IdempotencyKey);
            context.Stage(comment);
            context.Stage(new AuditEvent(Guid.NewGuid(), context.ActorId, "incident.comment.add", "AvailabilityIncident",
                context.Incident.Id, null, null, context.TrustedCorrelationId, context.OccurredAt, null));
            return new LockedCommandDecision<string>.Apply(new StagedCommandOutcome(comment.Id, "general_comment", context.OccurredAt));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new StoredIncidentHttpResponse(201, "{\"created\":true}"u8,
                "application/json; charset=utf-8", context.ResultingEtag));
    }

    private sealed class StateConflictHandler : IIncidentLockedCommandHandler<string, string>
    {
        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken)
        {
            context.Stage(new IncidentComment(Guid.NewGuid(), context.Incident.Id, context.Incident.ProbeId,
                context.ActorId, context.Command, context.OccurredAt, context.IdempotencyKey));
            return ValueTask.FromResult<LockedCommandDecision<string>>(new LockedCommandDecision<string>.StateConflict("state"));
        }

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("A state conflict must not construct a response.");
    }

    private sealed class ResponseFailureHandler : IIncidentLockedCommandHandler<string, string>
    {
        private readonly CommentHandler inner = new();

        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken) =>
            inner.ApplyAfterCurrentEtagAcceptedAsync(context, cancellationToken);

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("response construction failure");
    }

    private sealed class CommitThenIndeterminateBoundary(bool cancellation) : IIncidentCommitBoundary
    {
        public async Task CommitAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken);
            if (cancellation) throw new OperationCanceledException(new CancellationToken(canceled: true));
            throw new NpgsqlException("commit acknowledgement lost");
        }
    }

    private enum TransactionFault { Begin, Rollback, Dispose }

    private sealed class FaultingTransactionBoundary(TransactionFault fault) : IIncidentTransactionBoundary
    {
        public Task<IDbContextTransaction> BeginAsync(EePulseDbContext db, CancellationToken cancellationToken)
        {
            if (fault == TransactionFault.Begin)
                return Task.FromException<IDbContextTransaction>(new NpgsqlException("test transaction acquisition failure"));
            return db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        }

        public async Task RollbackAsync(IDbContextTransaction transaction)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (fault == TransactionFault.Rollback) throw new NpgsqlException("test rollback failure");
        }

        public async ValueTask DisposeAsync(IDbContextTransaction transaction)
        {
            await transaction.DisposeAsync();
            if (fault == TransactionFault.Dispose) throw new NpgsqlException("test disposal failure");
        }
    }

    private sealed class RecordingTransactionBoundary : IIncidentTransactionBoundary
    {
        public int RollbackCalls { get; private set; }

        public Task<IDbContextTransaction> BeginAsync(EePulseDbContext db, CancellationToken cancellationToken) =>
            db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);

        public async Task RollbackAsync(IDbContextTransaction transaction)
        {
            RollbackCalls++;
            await transaction.RollbackAsync(CancellationToken.None);
        }

        public ValueTask DisposeAsync(IDbContextTransaction transaction) => transaction.DisposeAsync();
    }

    private sealed class CollidingAdvisoryKeyProvider : IIncidentIdempotencyAdvisoryKeyProvider
    {
        public (int NamespaceKey, int DerivedKey) Derive(string idempotencyKey) => (IncidentIdempotencyAdvisoryKeyDeriver.NamespaceKey, 7);
    }

    private sealed class RuntimeCommandObserver : DbCommandInterceptor
    {
        private readonly List<string> commands = [];
        public IReadOnlyList<string> Commands => commands;
        public void Clear() => commands.Clear();

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result) { commands.Add(command.CommandText); return result; }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result) { commands.Add(command.CommandText); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) { commands.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    private sealed class ThrowingHandler : IIncidentLockedCommandHandler<string, string>
    {
        public ValueTask<LockedCommandDecision<string>> ApplyAfterCurrentEtagAcceptedAsync(
            LockedIncidentExecutionContext<string> context, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("A not-found command must not invoke the handler.");

        public ValueTask<StoredIncidentHttpResponse> CreateSuccessResponseAfterFirstFlushAsync(
            IncidentCommandResponseContext context, StagedCommandOutcome outcome, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("A not-found command must not construct a response.");
    }
}
