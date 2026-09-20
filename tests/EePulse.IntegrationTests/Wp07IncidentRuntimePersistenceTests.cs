using EePulse.Domain.Agents;
using EePulse.Domain.Identity;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Runtime.ExceptionServices;
using System.Transactions;

namespace EePulse.IntegrationTests;

public sealed class Wp07IncidentRuntimePersistenceTests
{
    private const string FoundationMigration = "20260914145214_WP07Phase2B2IncidentFoundation";
    private const string RuntimeMigration = "20260918084501_WP07Phase2B2IncidentRuntime";
    private const string ImmutableMessage = "WP07 incident runtime records are immutable.";
    private static readonly DateTimeOffset TestTime = new(2026, 9, 18, 8, 45, 1, TimeSpan.Zero);
    private static readonly int[] TrimCodePoints = [9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288];
    private static readonly string[] RuntimeConstraintNames =
    [
        "ck_availability_incidents_acknowledgement_comment_text",
        "ck_availability_incidents_resolution_note_text",
        "ck_idempotency_receipts_completed_at",
        "ck_idempotency_receipts_content_type_nonempty",
        "ck_idempotency_receipts_outcome_kind",
        "ck_idempotency_receipts_outcome_references",
        "ck_idempotency_receipts_outcome_status",
        "ck_idempotency_receipts_request_digest",
        "ck_idempotency_receipts_request_fingerprint_version",
        "ck_idempotency_receipts_response_etag_strong_quoted",
        "ck_idempotency_receipts_response_status_success",
        "ck_idempotency_receipts_response_status_valid",
        "ck_idempotency_receipts_route_nonempty",
        "ck_incident_comments_comment",
        "ck_incident_lifecycle_actions_action_note",
        "ck_incident_lifecycle_actions_kind_reason",
        "fk_idempotency_receipts_incident_comment_outcome",
        "fk_idempotency_receipts_lifecycle_action_outcome",
        "fk_incident_comments_availability_incident",
        "fk_incident_comments_human_principal",
        "fk_incident_comments_idempotency_receipt",
        "fk_incident_lifecycle_actions_availability_incident",
        "fk_incident_lifecycle_actions_human_principal",
        "fk_incident_lifecycle_actions_idempotency_receipt",
        "pk_idempotency_receipts",
        "pk_incident_comments",
        "pk_incident_lifecycle_actions",
        "uq_incident_comments_idempotency_key",
        "uq_incident_comments_receipt_outcome",
        "uq_incident_lifecycle_actions_idempotency_key",
        "uq_incident_lifecycle_actions_receipt_outcome"
    ];
    private static readonly string[] DeferredCycleForeignKeyNames =
    [
        "fk_idempotency_receipts_incident_comment_outcome",
        "fk_idempotency_receipts_lifecycle_action_outcome",
        "fk_incident_comments_idempotency_receipt",
        "fk_incident_lifecycle_actions_idempotency_receipt"
    ];
    private static readonly string[] NullableUniqueIndexNames =
    [
        "uq_idempotency_receipts_incident_comment",
        "uq_idempotency_receipts_lifecycle_action"
    ];

    [Fact]
    public async Task UpCreatesFrozenHelpersDeferredForeignKeysAndAppendOnlyTriggers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var db = CreateContext(postgres.ConnectionString);
        await db.Database.MigrateAsync(ct);
        Assert.Contains(RuntimeMigration, await db.Database.GetAppliedMigrationsAsync(ct));
        Assert.Equal(3, await ScalarAsync<int>(db, "SELECT COUNT(*)::int FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname IN ('wp07_dotnet_trim', 'wp07_dotnet_utf16_code_units', 'wp07_incident_text_is_valid')", ct));
        Assert.Equal(4, await ScalarAsync<int>(db, "SELECT COUNT(*)::int FROM pg_constraint WHERE conname IN ('fk_incident_comments_idempotency_receipt', 'fk_incident_lifecycle_actions_idempotency_receipt', 'fk_idempotency_receipts_incident_comment_outcome', 'fk_idempotency_receipts_lifecycle_action_outcome') AND confdeltype = 'a' AND condeferrable AND condeferred", ct));
        Assert.Equal(3, await ScalarAsync<int>(db, "SELECT COUNT(*)::int FROM pg_trigger WHERE NOT tgisinternal AND tgname IN ('tr_incident_comments_immutable', 'tr_incident_lifecycle_actions_immutable', 'tr_idempotency_receipts_immutable')", ct));
    }

    [Fact]
    public async Task DownRemovesRuntimeObjects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var db = CreateContext(postgres.ConnectionString);
        await db.Database.MigrateAsync(ct);
        await db.GetService<IMigrator>().MigrateAsync(FoundationMigration, ct);
        await AssertRuntimeObjectsAbsentAsync(db, ct);
    }

    [Theory]
    [InlineData("idempotency_receipts")]
    [InlineData("incident_comments")]
    [InlineData("incident_lifecycle_actions")]
    public async Task DownFailsAtomicallyWhenRuntimeTableLockIsUnavailable(string lockedTable)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using (var setup = CreateContext(postgres.ConnectionString)) await setup.Database.MigrateAsync(ct);
        await using var holder = new NpgsqlConnection(postgres.ConnectionString);
        await holder.OpenAsync(ct);
        await using var holderTransaction = await holder.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand($"LOCK TABLE public.{lockedTable} IN ACCESS SHARE MODE", holder, holderTransaction)) await lockCommand.ExecuteNonQueryAsync(ct);
        await using (var downgrade = CreateContext(postgres.ConnectionString))
        {
            using var behaviorLimit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var error = await AssertPostgresExceptionAsync(downgrade.GetService<IMigrator>().MigrateAsync(FoundationMigration, behaviorLimit.Token));
            Assert.Equal("55P03", error.SqlState);
        }
        await AssertRuntimeObjectsPresentAsync(postgres.ConnectionString, ct);
        await holderTransaction.RollbackAsync(ct);
        await using var completedDowngrade = CreateContext(postgres.ConnectionString);
        await completedDowngrade.GetService<IMigrator>().MigrateAsync(FoundationMigration, ct);
        await AssertRuntimeObjectsAbsentAsync(completedDowngrade, ct);
    }

    [Fact]
    public async Task Utf8PreflightRejectsLatin1FixtureBeforeRuntimeSchemaAndCleansItUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var adminConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var fixtureConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = "wp07_non_utf8_fixture", Pooling = false }.ConnectionString;
        await RunLatin1PreflightLifecycleAsync(adminConnectionString, fixtureConnectionString, ct);
    }

    [Fact]
    public async Task Latin1CreateAcknowledgementAmbiguityStillCleansFixture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var adminConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var fixtureConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = "wp07_non_utf8_fixture", Pooling = false }.ConnectionString;
        var sentinel = new Latin1CreateAcknowledgementSentinelException();

        var observed = await Assert.ThrowsAsync<Latin1CreateAcknowledgementSentinelException>(() =>
            RunLatin1PreflightLifecycleAsync(adminConnectionString, fixtureConnectionString, ct, () => throw sentinel));

        Assert.Same(sentinel, observed);
        await AssertLatin1FixtureAbsentAsync(adminConnectionString, ct);
    }

    private static async Task RunLatin1PreflightLifecycleAsync(
        string adminConnectionString,
        string fixtureConnectionString,
        CancellationToken ct,
        Func<Task>? afterCreateBeforeAcknowledgement = null)
    {
        var createAttempted = false;
        ExceptionDispatchInfo? primaryFailure = null;
        IReadOnlyList<ExceptionDispatchInfo> cleanupFailures = [];
        try
        {
            try
            {
                await using (var admin = new NpgsqlConnection(adminConnectionString))
                {
                    Assert.Null(Transaction.Current);
                    await admin.OpenAsync(ct);
                    await using (var currentUser = new NpgsqlCommand("SELECT current_user", admin)) Assert.Equal("postgres", (string)(await currentUser.ExecuteScalarAsync(ct))!);
                    await using (var capability = new NpgsqlCommand("SELECT rolcreatedb FROM pg_catalog.pg_roles WHERE rolname = current_user", admin)) Assert.True((bool)(await capability.ExecuteScalarAsync(ct))!);
                    await using (var transactionFree = new NpgsqlCommand("SELECT pg_catalog.txid_current_if_assigned() IS NULL", admin)) Assert.True((bool)(await transactionFree.ExecuteScalarAsync(ct))!);
                    await using (var absentBeforeCreate = new NpgsqlCommand("SELECT COUNT(*) FROM pg_catalog.pg_database WHERE datname = 'wp07_non_utf8_fixture'", admin)) Assert.Equal(0L, (long)(await absentBeforeCreate.ExecuteScalarAsync(ct))!);
                    await using var create = new NpgsqlCommand("CREATE DATABASE wp07_non_utf8_fixture WITH TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C';", admin);
                    Assert.Null(create.Transaction);
                    createAttempted = true;
                    await ExecuteCreateLatin1FixtureAsync(
                        create,
                        afterCreateBeforeAcknowledgement: afterCreateBeforeAcknowledgement,
                        ct: ct);
                }

                await using (var owner = new NpgsqlConnection(adminConnectionString))
                {
                    Assert.Null(Transaction.Current);
                    await owner.OpenAsync(ct);
                    await using (var exists = new NpgsqlCommand("SELECT COUNT(*) FROM pg_catalog.pg_database WHERE datname = 'wp07_non_utf8_fixture'", owner)) Assert.Equal(1L, (long)(await exists.ExecuteScalarAsync(ct))!);
                    await using var databaseOwner = new NpgsqlCommand("SELECT datdba = 'postgres'::regrole FROM pg_catalog.pg_database WHERE datname = 'wp07_non_utf8_fixture'", owner);
                    Assert.True((bool)(await databaseOwner.ExecuteScalarAsync(ct))!);
                }

                await using (var foundation = CreateContext(fixtureConnectionString)) await foundation.GetService<IMigrator>().MigrateAsync(FoundationMigration, ct);
                await using (var runtime = CreateContext(fixtureConnectionString))
                {
                    var error = await AssertPostgresExceptionAsync(runtime.GetService<IMigrator>().MigrateAsync(RuntimeMigration, ct));
                    Assert.Equal("55000", error.SqlState);
                    Assert.Equal("WP07 incident runtime requires UTF8 server encoding.", error.MessageText);
                    Assert.Equal("ck_wp07_incident_runtime_server_encoding_utf8", error.ConstraintName);
                }
                await using (var inspect = new NpgsqlConnection(fixtureConnectionString))
                {
                    await inspect.OpenAsync(ct);
                    await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM (SELECT p.oid FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname IN ('wp07_dotnet_trim', 'wp07_dotnet_utf16_code_units', 'wp07_incident_text_is_valid', 'fn_wp07_incident_runtime_reject_mutation') UNION ALL SELECT c.oid FROM pg_class c WHERE c.relname IN ('idempotency_receipts', 'incident_comments', 'incident_lifecycle_actions') UNION ALL SELECT t.oid FROM pg_trigger t WHERE t.tgname IN ('tr_incident_comments_immutable', 'tr_incident_lifecycle_actions_immutable', 'tr_idempotency_receipts_immutable') UNION ALL SELECT con.oid FROM pg_constraint con WHERE con.conname LIKE 'ck_idempotency_receipts_%' OR con.conname IN ('ck_incident_comments_comment', 'ck_incident_lifecycle_actions_action_note')) runtime_objects", inspect);
                    Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(ct))!);
                    await using var history = new NpgsqlCommand("SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = @id", inspect);
                    history.Parameters.AddWithValue("id", RuntimeMigration);
                    Assert.Equal(0L, (long)(await history.ExecuteScalarAsync(ct))!);
                }
            }
            catch (Exception error) { primaryFailure = ExceptionDispatchInfo.Capture(error); }
        }
        finally
        {
            if (createAttempted) cleanupFailures = await CleanupLatin1FixtureAsync(adminConnectionString);
        }

        if (primaryFailure is not null && cleanupFailures.Count == 0) primaryFailure.Throw();
        if (primaryFailure is not null) throw new AggregateException("The LATIN1 fixture test and its cleanup both failed.", [primaryFailure.SourceException, .. cleanupFailures.Select(failure => failure.SourceException)]);
        if (cleanupFailures.Count == 1) cleanupFailures[0].Throw();
        if (cleanupFailures.Count > 1) throw new AggregateException("LATIN1 fixture cleanup failed.", cleanupFailures.Select(failure => failure.SourceException));
    }

    // The hook runs after PostgreSQL completes CREATE but before this wrapper completes to its caller.
    // A legacy acknowledgement flag placed after this await would therefore still be false on hook failure.
    private static async Task ExecuteCreateLatin1FixtureAsync(
        NpgsqlCommand create,
        Func<Task>? afterCreateBeforeAcknowledgement,
        CancellationToken ct)
    {
        await create.ExecuteNonQueryAsync(ct);
        if (afterCreateBeforeAcknowledgement is not null) await afterCreateBeforeAcknowledgement();
    }

    [Fact]
    public async Task TextHelpersAndPersistedChecksEnforceFrozenUtf16AndTrimBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var db = CreateContext(postgres.ConnectionString);
        await db.Database.MigrateAsync(ct);
        var seed = await SeedRuntimeStateAsync(db, ct);
        var trimSet = string.Concat(TrimCodePoints.Select(char.ConvertFromUtf32));
        Assert.Equal("x" + trimSet + "y", await ScalarAsync<string>(db, "SELECT public.wp07_dotnet_trim(@p)", ct, trimSet + "x" + trimSet + "y" + trimSet));
        const string zeroWidthSpace = "\u200B";
        var zeroWidthSpaceInput = zeroWidthSpace + "x" + zeroWidthSpace + "y" + zeroWidthSpace;
        Assert.Equal(zeroWidthSpaceInput, await ScalarAsync<string>(db, "SELECT public.wp07_dotnet_trim(@p)", ct, zeroWidthSpaceInput));
        Assert.Equal("a\tb", await ScalarAsync<string>(db, "SELECT public.wp07_dotnet_trim(@p)", ct, "a\tb"));
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT public.wp07_dotnet_utf16_code_units(@p)", ct, "A"));
        Assert.Equal(2, await ScalarAsync<int>(db, "SELECT public.wp07_dotnet_utf16_code_units(@p)", ct, "😀"));
        Assert.Equal(3, await ScalarAsync<int>(db, "SELECT public.wp07_dotnet_utf16_code_units(@p)", ct, "A😀"));
        Assert.True(await ScalarAsync<bool>(db, "SELECT public.wp07_incident_text_is_valid(@p)", ct, new string('a', 2000)));
        Assert.False(await ScalarAsync<bool>(db, "SELECT public.wp07_incident_text_is_valid(@p)", ct, new string('a', 2001)));
        Assert.True(await ScalarAsync<bool>(db, "SELECT public.wp07_incident_text_is_valid(@p)", ct, string.Concat(Enumerable.Repeat("😀", 1000))));
        Assert.False(await ScalarAsync<bool>(db, "SELECT public.wp07_incident_text_is_valid(@p)", ct, string.Concat(Enumerable.Repeat("😀", 1001))));
        foreach (var forbidden in new[] { "\u0001", "\u0085", "\u009F", "\u2028", "\u2029" }) Assert.False(await ScalarAsync<bool>(db, "SELECT public.wp07_incident_text_is_valid(@p)", ct, "valid" + forbidden));
        await InsertValidCommentOutcomeAsync(postgres.ConnectionString, seed, ct);
        await InsertValidActionOutcomeAsync(postgres.ConnectionString, seed, "acknowledgement", "operator-acknowledgement", ct);
        await AssertConstraintAsync(postgres.ConnectionString, CommentInsertSql(seed, " invalid", NewKey()), "ck_incident_comments_comment", ct);
        await AssertConstraintAsync(postgres.ConnectionString, CommentInsertSql(seed, "", NewKey()), "ck_incident_comments_comment", ct);
        await AssertConstraintAsync(postgres.ConnectionString, CommentInsertSql(seed, new string('a', 2001), NewKey()), "ck_incident_comments_comment", ct);
        await AssertConstraintAsync(postgres.ConnectionString, ActionInsertSql(seed, "acknowledgement", "operator-acknowledgement", " invalid", NewKey()), "ck_incident_lifecycle_actions_action_note", ct);
        await AssertConstraintAsync(postgres.ConnectionString, ActionInsertSql(seed, "acknowledgement", "operator-acknowledgement", "", NewKey()), "ck_incident_lifecycle_actions_action_note", ct);
        await AssertConstraintAsync(postgres.ConnectionString, ActionInsertSql(seed, "acknowledgement", "operator-acknowledgement", new string('a', 2001), NewKey()), "ck_incident_lifecycle_actions_action_note", ct);
        await ExecuteAsync(postgres.ConnectionString, $"UPDATE availability_incidents SET status = 'Acknowledged', acknowledged_at = TIMESTAMPTZ '2026-09-18 08:45:02+00', acknowledged_by = '{seed.ActorId}', acknowledgement_comment = 'valid acknowledgement' WHERE id = '{seed.IncidentId}'", ct);
        var acknowledgedBefore = await RowSnapshotAsync(postgres.ConnectionString, "availability_incidents", "id", seed.IncidentId, ct);
        await AssertConstraintAsync(postgres.ConnectionString, $"UPDATE availability_incidents SET acknowledgement_comment = ' invalid' WHERE id = '{seed.IncidentId}'", "ck_availability_incidents_acknowledgement_comment_text", ct);
        Assert.Equal(acknowledgedBefore, await RowSnapshotAsync(postgres.ConnectionString, "availability_incidents", "id", seed.IncidentId, ct));
        await ExecuteAsync(postgres.ConnectionString, $"UPDATE availability_incidents SET status = 'Resolved', acknowledged_at = NULL, acknowledged_by = NULL, acknowledgement_comment = NULL, resolved_at = TIMESTAMPTZ '2026-09-18 08:45:03+00', resolved_by = '{seed.ActorId}', resolution_note = 'valid resolution' WHERE id = '{seed.IncidentId}'", ct);
        var resolvedBefore = await RowSnapshotAsync(postgres.ConnectionString, "availability_incidents", "id", seed.IncidentId, ct);
        await AssertConstraintAsync(postgres.ConnectionString, $"UPDATE availability_incidents SET resolution_note = E'bad\\001' WHERE id = '{seed.IncidentId}'", "ck_availability_incidents_resolution_note_text", ct);
        Assert.Equal(resolvedBefore, await RowSnapshotAsync(postgres.ConnectionString, "availability_incidents", "id", seed.IncidentId, ct));
        await ExecuteAsync(postgres.ConnectionString, $"UPDATE availability_incidents SET status = 'Resolved', acknowledged_at = TIMESTAMPTZ '2026-09-18 08:45:02+00', acknowledged_by = '{seed.ActorId}', acknowledgement_comment = '{new string('a', 2000)}', resolved_at = TIMESTAMPTZ '2026-09-18 08:45:03+00', resolved_by = '{seed.ActorId}', resolution_note = '{new string('b', 2000)}' WHERE id = '{seed.IncidentId}'", ct);
    }

    [Fact]
    public async Task DirectSqlEnforcesReceiptInvariantsAndAcceptsEveryOutcomeShape()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var db = CreateContext(postgres.ConnectionString);
        await db.Database.MigrateAsync(ct);
        var seed = await SeedRuntimeStateAsync(db, ct);
        var comment = await InsertValidCommentOutcomeAsync(postgres.ConnectionString, seed, ct);
        var acknowledgement = await InsertValidActionOutcomeAsync(postgres.ConnectionString, seed, "acknowledgement", "operator-acknowledgement", ct);
        var resolution = await InsertValidActionOutcomeAsync(postgres.ConnectionString, seed, "manual_resolution", "manual-resolution", ct);
        Assert.All(new[] { comment, acknowledgement, resolution }, item => Assert.NotEmpty(item.Key));
        await AssertConstraintAsync(postgres.ConnectionString, ReceiptWithCommentSql(seed, NewKey(), Guid.NewGuid(), requestVersion: 2), "ck_idempotency_receipts_request_fingerprint_version", ct);
        await AssertConstraintAsync(postgres.ConnectionString, ReceiptWithCommentSql(seed, NewKey(), Guid.NewGuid(), digestHex: "00"), "ck_idempotency_receipts_request_digest", ct);
        var responseStatusSuccessKey = NewKey();
        await AssertConstraintWithTransactionalIsolationAsync(postgres.ConnectionString,
            ReceiptWithCommentSql(seed, responseStatusSuccessKey, Guid.NewGuid(), responseStatus: 204),
            "23514", "ck_idempotency_receipts_response_status_success", responseStatusSuccessKey,
            ["ck_idempotency_receipts_outcome_status"], ct);
        await AssertConstraintAsync(postgres.ConnectionString, ReceiptWithCommentSql(seed, NewKey(), Guid.NewGuid(), responseStatus: 200), "ck_idempotency_receipts_outcome_status", ct);
        var outcomeKindKey = NewKey();
        await AssertConstraintWithTransactionalIsolationAsync(postgres.ConnectionString,
            ReceiptWithActionSql(seed, outcomeKindKey, Guid.NewGuid(), "other", "acknowledgement", "operator-acknowledgement"),
            "23514", "ck_idempotency_receipts_outcome_kind", outcomeKindKey,
            ["ck_idempotency_receipts_outcome_status", "ck_idempotency_receipts_outcome_references", "fk_idempotency_receipts_lifecycle_action_outcome"], ct);
        await AssertConstraintAsync(postgres.ConnectionString, ReceiptInsertSql(seed, NewKey(), "general_comment", 201, null, null), "ck_idempotency_receipts_outcome_references", ct);
        await AssertConstraintAsync(postgres.ConnectionString, ReceiptWithCommentSql(seed, NewKey(), Guid.NewGuid(), contentType: ""), "ck_idempotency_receipts_content_type_nonempty", ct);
        await AssertConstraintAsync(postgres.ConnectionString, ReceiptWithCommentSql(seed, NewKey(), Guid.NewGuid(), etag: "W/\"tag\""), "ck_idempotency_receipts_response_etag_strong_quoted", ct);
        await AssertConstraintAsync(postgres.ConnectionString, ReceiptWithCommentSql(seed, NewKey(), Guid.NewGuid(), completedBeforeCreated: true), "ck_idempotency_receipts_completed_at", ct);
        var nullActorKey = NewKey();
        var nullActorCommentId = Guid.NewGuid();
        await AssertSqlStateAndColumnAsync(postgres.ConnectionString,
            $"{ReceiptInsertSql(seed, nullActorKey, "general_comment", 201, nullActorCommentId, null).Replace($"'{seed.ActorId}'", "NULL")} {CommentInsertSql(seed, "valid comment", nullActorKey, nullActorCommentId)}",
            "23502", "actor_id", ct);
        var nullBodyKey = NewKey();
        var nullBodyCommentId = Guid.NewGuid();
        await AssertSqlStateAndColumnAsync(postgres.ConnectionString,
            $"{ReceiptInsertSql(seed, nullBodyKey, "general_comment", 201, nullBodyCommentId, null).Replace("decode('0102', 'hex')", "NULL")} {CommentInsertSql(seed, "valid comment", nullBodyKey, nullBodyCommentId)}",
            "23502", "response_body", ct);
        var nullCreatedAtKey = NewKey();
        var nullCreatedAtCommentId = Guid.NewGuid();
        await AssertSqlStateAndColumnAsync(postgres.ConnectionString,
            $"{ReceiptInsertSql(seed, nullCreatedAtKey, "general_comment", 201, nullCreatedAtCommentId, null).Replace("TIMESTAMPTZ '2026-09-18 08:45:02+00'", "NULL")} {CommentInsertSql(seed, "valid comment", nullCreatedAtKey, nullCreatedAtCommentId)}",
            "23502", "created_at", ct);
        var commentUniqueKey = NewKey();
        await AssertConstraintWithTransactionalIsolationAsync(postgres.ConnectionString,
            ReceiptInsertSql(seed, commentUniqueKey, "general_comment", 201, comment.ChildId, null),
            "23505", "uq_idempotency_receipts_incident_comment", commentUniqueKey,
            ["fk_idempotency_receipts_incident_comment_outcome"], ct);
        var actionUniqueKey = NewKey();
        await AssertConstraintWithTransactionalIsolationAsync(postgres.ConnectionString,
            ReceiptInsertSql(seed, actionUniqueKey, "acknowledgement", 200, null, acknowledgement.ChildId),
            "23505", "uq_idempotency_receipts_lifecycle_action", actionUniqueKey,
            ["fk_idempotency_receipts_lifecycle_action_outcome"], ct);
        await AssertDeferredConstraintAsync(postgres.ConnectionString, ReceiptAndCommentWithMismatchedActorSql(seed, NewKey()), "fk_idempotency_receipts_incident_comment_outcome", ct);
        await AssertDeferredConstraintAsync(postgres.ConnectionString, ReceiptAndActionWithMismatchedKindSql(seed, NewKey()), "fk_idempotency_receipts_lifecycle_action_outcome", ct);
    }

    [Fact]
    public async Task AppendOnlyTriggersRejectDirectUpdatesAndDeletesWithoutChangingRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        await using var db = CreateContext(postgres.ConnectionString);
        await db.Database.MigrateAsync(ct);
        var seed = await SeedRuntimeStateAsync(db, ct);
        var comment = await InsertValidCommentOutcomeAsync(postgres.ConnectionString, seed, ct);
        var action = await InsertValidActionOutcomeAsync(postgres.ConnectionString, seed, "acknowledgement", "operator-acknowledgement", ct);
        await AssertImmutableAsync(postgres.ConnectionString, "incident_comments", "id", comment.ChildId, "tr_incident_comments_immutable", ct);
        await AssertImmutableAsync(postgres.ConnectionString, "incident_lifecycle_actions", "event_id", action.ChildId, "tr_incident_lifecycle_actions_immutable", ct);
        await AssertImmutableAsync(postgres.ConnectionString, "idempotency_receipts", "idempotency_key", comment.Key, "tr_idempotency_receipts_immutable", ct);
    }

    private static EePulseDbContext CreateContext(string connectionString) => new(new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options);
    private static async Task<RuntimeSeed> SeedRuntimeStateAsync(EePulseDbContext db, CancellationToken ct)
    {
        var site = new Site(Guid.NewGuid(), "WP07", "Runtime test site", "UTC", TestTime);
        var group = new AgentGroup(Guid.NewGuid(), "Runtime test group", null, TestTime);
        var device = new Device(Guid.NewGuid(), site.Id, "Runtime test device", "192.0.2.70", null, "PLC", null, null, Criticality.Normal, [], TestTime);
        var probe = new Probe(Guid.NewGuid(), device.Id, group.Id, 30, 2000, 3, 500, null, 3, 2);
        var actor = new HumanPrincipal(Guid.NewGuid(), "https://issuer.example.test/runtime", "runtime-actor", TestTime);
        var incident = new AvailabilityIncident(Guid.NewGuid(), probe.Id, TestTime);
        db.AddRange(site, group, device, probe, actor, incident);
        await db.SaveChangesAsync(ct);
        return new RuntimeSeed(incident.Id, probe.Id, actor.Id);
    }

    private static async Task<OutcomeRow> InsertValidCommentOutcomeAsync(string connectionString, RuntimeSeed seed, CancellationToken ct)
    {
        var key = NewKey(); var childId = Guid.NewGuid();
        await ExecuteInTransactionAsync(connectionString, $"{ReceiptInsertSql(seed, key, "general_comment", 201, childId, null)} {CommentInsertSql(seed, "valid comment", key, childId)}", ct);
        return new OutcomeRow(key, childId);
    }
    private static async Task<OutcomeRow> InsertValidActionOutcomeAsync(string connectionString, RuntimeSeed seed, string kind, string reason, CancellationToken ct)
    {
        var key = NewKey(); var childId = Guid.NewGuid();
        await ExecuteInTransactionAsync(connectionString, $"{ReceiptInsertSql(seed, key, kind, 200, null, childId)} {ActionInsertSql(seed, kind, reason, "valid action", key, childId)}", ct);
        return new OutcomeRow(key, childId);
    }
    private static string ReceiptInsertSql(RuntimeSeed seed, string key, string outcomeKind, int responseStatus, Guid? commentId, Guid? actionId, short requestVersion = 1, string digestHex = "0000000000000000000000000000000000000000000000000000000000000000", string contentType = "application/json; charset=utf-8", string etag = "\"!\"", bool completedBeforeCreated = false) =>
        $"INSERT INTO idempotency_receipts (idempotency_key, route_template, incident_id, actor_id, request_fingerprint_version, request_digest, response_status, response_body, response_content_type, response_etag, outcome_kind, incident_comment_id, incident_lifecycle_action_id, created_at, completed_at) VALUES ('{key}', '/api/v1/incidents/{{id}}/comments', '{seed.IncidentId}', '{seed.ActorId}', {requestVersion}, decode('{digestHex}', 'hex'), {responseStatus}, decode('0102', 'hex'), '{contentType}', '{etag}', '{outcomeKind}', {NullableGuid(commentId)}, {NullableGuid(actionId)}, TIMESTAMPTZ '2026-09-18 08:45:02+00', TIMESTAMPTZ '{(completedBeforeCreated ? "2026-09-18 08:45:01+00" : "2026-09-18 08:45:03+00")}');";
    private static string ReceiptWithCommentSql(RuntimeSeed seed, string key, Guid commentId, string outcomeKind = "general_comment", int responseStatus = 201, short requestVersion = 1, string digestHex = "0000000000000000000000000000000000000000000000000000000000000000", string contentType = "application/json; charset=utf-8", string etag = "\"!\"", bool completedBeforeCreated = false) =>
        $"{ReceiptInsertSql(seed, key, outcomeKind, responseStatus, commentId, null, requestVersion, digestHex, contentType, etag, completedBeforeCreated)} {CommentInsertSql(seed, "valid comment", key, commentId)}";
    private static string ReceiptWithActionSql(RuntimeSeed seed, string key, Guid actionId, string outcomeKind, string actionKind, string reasonCode) =>
        $"{ReceiptInsertSql(seed, key, outcomeKind, 200, null, actionId)} {ActionInsertSql(seed, actionKind, reasonCode, "valid action", key, actionId)}";
    private static string CommentInsertSql(RuntimeSeed seed, string comment, string key, Guid? id = null) => $"INSERT INTO incident_comments (id, incident_id, probe_id, author_id, comment, created_at, idempotency_key) VALUES ('{id ?? Guid.NewGuid()}', '{seed.IncidentId}', '{seed.ProbeId}', '{seed.ActorId}', '{comment}', TIMESTAMPTZ '2026-09-18 08:45:04+00', '{key}');";
    private static string ActionInsertSql(RuntimeSeed seed, string kind, string reason, string note, string key, Guid? id = null) => $"INSERT INTO incident_lifecycle_actions (event_id, incident_id, probe_id, actor_id, action_kind, reason_code, occurred_at, action_note, idempotency_key) VALUES ('{id ?? Guid.NewGuid()}', '{seed.IncidentId}', '{seed.ProbeId}', '{seed.ActorId}', '{kind}', '{reason}', TIMESTAMPTZ '2026-09-18 08:45:04+00', '{note}', '{key}');";
    private static string ReceiptAndCommentWithMismatchedActorSql(RuntimeSeed seed, string key)
    {
        var otherActor = Guid.NewGuid(); var childId = Guid.NewGuid();
        return $"INSERT INTO human_principals (id, issuer, subject, created_at) VALUES ('{otherActor}', 'https://issuer.example.test/other', 'other-actor', TIMESTAMPTZ '2026-09-18 08:45:00+00'); {ReceiptInsertSql(seed, key, "general_comment", 201, childId, null)} INSERT INTO incident_comments (id, incident_id, probe_id, author_id, comment, created_at, idempotency_key) VALUES ('{childId}', '{seed.IncidentId}', '{seed.ProbeId}', '{otherActor}', 'valid comment', TIMESTAMPTZ '2026-09-18 08:45:04+00', '{key}');";
    }
    private static string ReceiptAndActionWithMismatchedKindSql(RuntimeSeed seed, string key)
    {
        var childId = Guid.NewGuid();
        return $"{ReceiptInsertSql(seed, key, "manual_resolution", 200, null, childId)} INSERT INTO incident_lifecycle_actions (event_id, incident_id, probe_id, actor_id, action_kind, reason_code, occurred_at, action_note, idempotency_key) VALUES ('{childId}', '{seed.IncidentId}', '{seed.ProbeId}', '{seed.ActorId}', 'acknowledgement', 'operator-acknowledgement', TIMESTAMPTZ '2026-09-18 08:45:04+00', 'valid action', '{key}');";
    }
    private static async Task AssertImmutableAsync(string cs, string table, string keyColumn, object key, string trigger, CancellationToken ct)
    {
        var before = await RowSnapshotAsync(cs, table, keyColumn, key, ct);
        foreach (var verb in new[] { "UPDATE", "DELETE" })
        {
            var sql = verb == "UPDATE" ? $"UPDATE {table} SET {keyColumn} = {SqlLiteral(key)} WHERE {keyColumn} = {SqlLiteral(key)}" : $"DELETE FROM {table} WHERE {keyColumn} = {SqlLiteral(key)}";
            await AssertSqlStateAndConstraintAsync(cs, sql, "23514", trigger, ct, ImmutableMessage);
            Assert.Equal(before, await RowSnapshotAsync(cs, table, keyColumn, key, ct));
        }
    }
    private static async Task<string> RowSnapshotAsync(string cs, string table, string keyColumn, object key, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT row_to_json(t)::text FROM {table} t WHERE {keyColumn} = {SqlLiteral(key)}", connection);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }
    private static async Task AssertConstraintAsync(string cs, string sql, string constraint, CancellationToken ct) => await AssertSqlStateAndConstraintAsync(cs, sql, "23514", constraint, ct);
    private static async Task AssertSqlStateAndColumnAsync(string cs, string sql, string state, string column, CancellationToken ct)
    {
        var error = await ExecuteExpectingPostgresExceptionAsync(cs, sql, ct); Assert.Equal(state, error.SqlState); Assert.Equal(column, error.ColumnName);
    }
    private static async Task AssertSqlStateAndConstraintAsync(string cs, string sql, string state, string constraint, CancellationToken ct, string? message = null)
    {
        var error = await ExecuteExpectingPostgresExceptionAsync(cs, sql, ct); Assert.Equal(state, error.SqlState); Assert.Equal(constraint, error.ConstraintName); if (message is not null) Assert.Equal(message, error.MessageText);
    }
    private static async Task AssertConstraintWithTransactionalIsolationAsync(
        string cs, string sql, string state, string constraint, string receiptKey, IReadOnlyList<string> constraintsToDrop, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var constraintToDrop in constraintsToDrop)
        {
            await using var drop = new NpgsqlCommand($"ALTER TABLE public.idempotency_receipts DROP CONSTRAINT {constraintToDrop};", connection, transaction);
            await drop.ExecuteNonQueryAsync(ct);
        }

        await using (var remaining = new NpgsqlCommand("SELECT COUNT(*)::int FROM pg_constraint WHERE conrelid = 'public.idempotency_receipts'::regclass AND conname = 'ck_idempotency_receipts_response_status_success';", connection, transaction))
        {
            Assert.Equal(1, (int)(await remaining.ExecuteScalarAsync(ct))!);
        }

        const string savepoint = "before_invalid_receipt";
        await transaction.SaveAsync(savepoint, ct);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            var error = await AssertPostgresExceptionAsync(command.ExecuteNonQueryAsync(ct));
            Assert.Equal(state, error.SqlState);
            Assert.Equal(constraint, error.ConstraintName);
        }

        await transaction.RollbackAsync(savepoint, ct);
        await using (var receipt = new NpgsqlCommand("SELECT COUNT(*)::int FROM idempotency_receipts WHERE idempotency_key = @key;", connection, transaction))
        {
            receipt.Parameters.AddWithValue("key", receiptKey);
            Assert.Equal(0, (int)(await receipt.ExecuteScalarAsync(ct))!);
        }

        await transaction.RollbackAsync(ct);
        await using var fresh = new NpgsqlConnection(cs);
        await fresh.OpenAsync(ct);
        foreach (var constraintToDrop in constraintsToDrop)
        {
            await using var restored = new NpgsqlCommand("SELECT COUNT(*)::int FROM pg_constraint WHERE conrelid = 'public.idempotency_receipts'::regclass AND conname = @constraint;", fresh);
            restored.Parameters.AddWithValue("constraint", constraintToDrop);
            Assert.Equal(1, (int)(await restored.ExecuteScalarAsync(ct))!);
        }
        await using var responseStatusSuccess = new NpgsqlCommand("SELECT COUNT(*)::int FROM pg_constraint WHERE conrelid = 'public.idempotency_receipts'::regclass AND conname = 'ck_idempotency_receipts_response_status_success';", fresh);
        Assert.Equal(1, (int)(await responseStatusSuccess.ExecuteScalarAsync(ct))!);
        await using var absent = new NpgsqlCommand("SELECT COUNT(*)::int FROM idempotency_receipts WHERE idempotency_key = @key;", fresh);
        absent.Parameters.AddWithValue("key", receiptKey);
        Assert.Equal(0, (int)(await absent.ExecuteScalarAsync(ct))!);
    }
    private static async Task AssertDeferredConstraintAsync(string cs, string sql, string constraint, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection, transaction)) await command.ExecuteNonQueryAsync(ct);
        var error = await AssertPostgresExceptionAsync(transaction.CommitAsync(ct)); Assert.Equal("23503", error.SqlState); Assert.Equal(constraint, error.ConstraintName);
    }
    private static async Task<PostgresException> ExecuteExpectingPostgresExceptionAsync(string cs, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync(ct); await using var command = new NpgsqlCommand(sql, connection);
        return await AssertPostgresExceptionAsync(command.ExecuteNonQueryAsync(ct));
    }
    private static async Task<PostgresException> AssertPostgresExceptionAsync(Task operation)
    {
        try { await operation; } catch (Exception error) { for (var current = error; current is not null; current = current.InnerException) if (current is PostgresException postgres) return postgres; throw; }
        throw new Xunit.Sdk.XunitException("Expected a PostgreSQL exception.");
    }
    private static async Task ExecuteAsync(string cs, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync(ct); await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task ExecuteInTransactionAsync(string cs, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(cs); await connection.OpenAsync(ct); await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection, transaction)) await command.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
    }
    private static async Task<T> ScalarAsync<T>(EePulseDbContext db, string sql, CancellationToken ct, string? value = null)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection(); var opened = connection.State != System.Data.ConnectionState.Open; if (opened) await connection.OpenAsync(ct);
        try { await using var command = new NpgsqlCommand(sql, connection); if (value is not null) command.Parameters.AddWithValue("p", value); return (T)(await command.ExecuteScalarAsync(ct))!; }
        finally { if (opened) await connection.CloseAsync(); }
    }
    private static async Task<IReadOnlyList<ExceptionDispatchInfo>> CleanupLatin1FixtureAsync(string adminConnectionString)
    {
        using var cleanupLimit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cleanupFailures = new List<ExceptionDispatchInfo>();
        bool? fixtureExists = null;

        try
        {
            await using var existenceAdmin = new NpgsqlConnection(adminConnectionString);
            Assert.Null(Transaction.Current);
            await existenceAdmin.OpenAsync(cleanupLimit.Token);
            await using var exists = new NpgsqlCommand("SELECT COUNT(*) FROM pg_catalog.pg_database WHERE datname = 'wp07_non_utf8_fixture'", existenceAdmin);
            Assert.Null(exists.Transaction);
            var count = (long)(await exists.ExecuteScalarAsync(cleanupLimit.Token))!;
            Assert.InRange(count, 0L, 1L);
            fixtureExists = count == 1;
        }
        catch (Exception error) { cleanupFailures.Add(ExceptionDispatchInfo.Capture(error)); }

        if (fixtureExists is true)
        {
            try
            {
                await using var sessionsAdmin = new NpgsqlConnection(adminConnectionString);
                Assert.Null(Transaction.Current);
                await sessionsAdmin.OpenAsync(cleanupLimit.Token);
                await using var sessions = new NpgsqlCommand("SELECT COUNT(*) FROM pg_catalog.pg_stat_activity WHERE datname = 'wp07_non_utf8_fixture'", sessionsAdmin);
                Assert.Null(sessions.Transaction);
                Assert.Equal(0L, (long)(await sessions.ExecuteScalarAsync(cleanupLimit.Token))!);
            }
            catch (Exception error) { cleanupFailures.Add(ExceptionDispatchInfo.Capture(error)); }
        }

        if (fixtureExists is not false)
        {
            try
            {
                await using var dropAdmin = new NpgsqlConnection(adminConnectionString);
                Assert.Null(Transaction.Current);
                await dropAdmin.OpenAsync(cleanupLimit.Token);
                await using var drop = new NpgsqlCommand(fixtureExists is true ? "DROP DATABASE wp07_non_utf8_fixture;" : "DROP DATABASE IF EXISTS wp07_non_utf8_fixture;", dropAdmin);
                Assert.Null(drop.Transaction);
                await drop.ExecuteNonQueryAsync(cleanupLimit.Token);
            }
            catch (Exception error) { cleanupFailures.Add(ExceptionDispatchInfo.Capture(error)); }

            try
            {
                await using var verifyAdmin = new NpgsqlConnection(adminConnectionString);
                Assert.Null(Transaction.Current);
                await verifyAdmin.OpenAsync(cleanupLimit.Token);
                await using var absent = new NpgsqlCommand("SELECT COUNT(*) FROM pg_catalog.pg_database WHERE datname = 'wp07_non_utf8_fixture'", verifyAdmin);
                Assert.Null(absent.Transaction);
                Assert.Equal(0L, (long)(await absent.ExecuteScalarAsync(cleanupLimit.Token))!);
            }
            catch (Exception error) { cleanupFailures.Add(ExceptionDispatchInfo.Capture(error)); }
        }

        return cleanupFailures;
    }

    private static async Task AssertLatin1FixtureAbsentAsync(string adminConnectionString, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        Assert.Null(Transaction.Current);
        await admin.OpenAsync(ct);
        await using var absent = new NpgsqlCommand("SELECT COUNT(*) FROM pg_catalog.pg_database WHERE datname = 'wp07_non_utf8_fixture'", admin);
        Assert.Null(absent.Transaction);
        Assert.Equal(0L, (long)(await absent.ExecuteScalarAsync(ct))!);
    }

    private static async Task AssertRuntimeObjectsPresentAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var migration = new NpgsqlCommand("SELECT COUNT(*)::int FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = @id", connection))
        {
            migration.Parameters.AddWithValue("id", RuntimeMigration);
            Assert.Equal(1, (int)(await migration.ExecuteScalarAsync(ct))!);
        }

        await using (var tables = new NpgsqlCommand("SELECT COUNT(*)::int FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname IN ('idempotency_receipts', 'incident_comments', 'incident_lifecycle_actions')", connection)) Assert.Equal(3, (int)(await tables.ExecuteScalarAsync(ct))!);
        await using (var functions = new NpgsqlCommand("SELECT COUNT(*)::int FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname IN ('wp07_dotnet_trim', 'wp07_dotnet_utf16_code_units', 'wp07_incident_text_is_valid', 'fn_wp07_incident_runtime_reject_mutation')", connection)) Assert.Equal(4, (int)(await functions.ExecuteScalarAsync(ct))!);
        await using (var triggers = new NpgsqlCommand("SELECT COUNT(*)::int FROM pg_catalog.pg_trigger WHERE NOT tgisinternal AND tgname IN ('tr_incident_comments_immutable', 'tr_incident_lifecycle_actions_immutable', 'tr_idempotency_receipts_immutable')", connection)) Assert.Equal(3, (int)(await triggers.ExecuteScalarAsync(ct))!);

        await using (var constraints = new NpgsqlCommand("""
            SELECT c.conname
            FROM pg_catalog.pg_constraint c
            JOIN pg_catalog.pg_class r ON r.oid = c.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = r.relnamespace
            WHERE n.nspname = 'public'
              AND c.contype IN ('p', 'u', 'f', 'c')
              AND (r.relname IN ('idempotency_receipts', 'incident_comments', 'incident_lifecycle_actions')
                   OR (r.relname = 'availability_incidents' AND c.conname IN ('ck_availability_incidents_acknowledgement_comment_text', 'ck_availability_incidents_resolution_note_text')))
            ORDER BY c.conname
            """, connection))
        {
            var actual = new List<string>();
            await using var reader = await constraints.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0));
            Assert.Equal(RuntimeConstraintNames, actual);
        }

        await using (var deferredCycles = new NpgsqlCommand("SELECT conname FROM pg_catalog.pg_constraint WHERE conname = ANY(@names) AND condeferrable AND condeferred ORDER BY conname", connection))
        {
            deferredCycles.Parameters.AddWithValue("names", DeferredCycleForeignKeyNames);
            var actual = new List<string>();
            await using var reader = await deferredCycles.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0));
            Assert.Equal(DeferredCycleForeignKeyNames.OrderBy(name => name), actual);
        }

        await using (var nullableUniqueIndexes = new NpgsqlCommand("SELECT i.relname FROM pg_catalog.pg_index x JOIN pg_catalog.pg_class i ON i.oid = x.indexrelid JOIN pg_catalog.pg_namespace n ON n.oid = i.relnamespace WHERE n.nspname = 'public' AND i.relname = ANY(@names) AND x.indisunique ORDER BY i.relname", connection))
        {
            nullableUniqueIndexes.Parameters.AddWithValue("names", NullableUniqueIndexNames);
            var actual = new List<string>();
            await using var reader = await nullableUniqueIndexes.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0));
            Assert.Equal(NullableUniqueIndexNames.OrderBy(name => name), actual);
        }
    }
    private static async Task AssertRuntimeObjectsAbsentAsync(EePulseDbContext db, CancellationToken ct)
    {
        Assert.DoesNotContain(RuntimeMigration, await db.Database.GetAppliedMigrationsAsync(ct));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*)::int FROM pg_class WHERE relname IN ('idempotency_receipts', 'incident_comments', 'incident_lifecycle_actions')", ct));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*)::int FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'public' AND p.proname IN ('wp07_dotnet_trim', 'wp07_dotnet_utf16_code_units', 'wp07_incident_text_is_valid', 'fn_wp07_incident_runtime_reject_mutation')", ct));
    }
    private static string NullableGuid(Guid? value) => value is null ? "NULL" : $"'{value}'";
    private static string NewKey() => Guid.NewGuid().ToString("D");
    private static string SqlLiteral(object value) => $"'{value}'";
    private sealed class Latin1CreateAcknowledgementSentinelException : Exception { }
    private sealed record RuntimeSeed(Guid IncidentId, Guid ProbeId, Guid ActorId);
    private sealed record OutcomeRow(string Key, Guid ChildId);
}
