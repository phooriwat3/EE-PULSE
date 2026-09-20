using System.Runtime.ExceptionServices;
using System.Text;
using EePulse.Domain.Agents;
using EePulse.Domain.Identity;
using EePulse.Domain.Inventory;
using EePulse.Domain.Preferences;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EePulse.IntegrationTests;

public sealed class Wp07IncidentPersistenceTests
{
    private const string BaselineMigration = "20260907125526_WP07UserTimezonePreferencePersistence";
    private const string IncidentFoundationMigration = "20260914145214_WP07Phase2B2IncidentFoundation";
    private const string C1ControlCharacterSetSql = "chr(128) || chr(129) || chr(130) || chr(131) || chr(132) || chr(133) || chr(134) || chr(135) || chr(136) || chr(137) || chr(138) || chr(139) || chr(140) || chr(141) || chr(142) || chr(143) || chr(144) || chr(145) || chr(146) || chr(147) || chr(148) || chr(149) || chr(150) || chr(151) || chr(152) || chr(153) || chr(154) || chr(155) || chr(156) || chr(157) || chr(158) || chr(159)";
    private const string HumanPrincipalImmutabilityTriggerName = "tr_human_principals_immutable";
    private const string HumanPrincipalImmutabilityFunctionName = "fn_wp07_human_principals_reject_mutation";
    private const string HumanPrincipalImmutabilityMessage = "WP07 human principals are immutable.";

    [Fact]
    public async Task EfModelMapsHumanPrincipalAndIncidentPersistenceFoundation()
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>()
            .UseNpgsql("Host=localhost;Database=wp07_model_only;Username=postgres;Password=postgres")
            .Options;
        await using var db = new EePulseDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var principal = model.FindEntityType(typeof(HumanPrincipal))!;
        var principalTable = StoreObjectIdentifier.Table("human_principals", null);

        Assert.Equal("human_principals", principal.GetTableName());
        Assert.Equal("uuid", principal.FindProperty(nameof(HumanPrincipal.Id))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal("character varying(512)", principal.FindProperty(nameof(HumanPrincipal.Issuer))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal("character varying(512)", principal.FindProperty(nameof(HumanPrincipal.Subject))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal("timestamp with time zone", principal.FindProperty(nameof(HumanPrincipal.CreatedAt))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal("C", principal.FindProperty(nameof(HumanPrincipal.Issuer))!.GetCollation());
        Assert.Equal("C", principal.FindProperty(nameof(HumanPrincipal.Subject))!.GetCollation());
        Assert.Equal("id", principal.FindPrimaryKey()!.Properties.Single().GetColumnName(principalTable));
        var principalChecks = principal.GetCheckConstraints().ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql);
        Assert.Contains($"translate(issuer, {C1ControlCharacterSetSql}, '') = issuer", principalChecks["ck_human_principals_issuer_shape"], StringComparison.Ordinal);
        Assert.Contains($"translate(subject, {C1ControlCharacterSetSql}, '') = subject", principalChecks["ck_human_principals_subject_shape"], StringComparison.Ordinal);
        var pairIndex = Assert.Single(principal.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(HumanPrincipal.Issuer), nameof(HumanPrincipal.Subject)]));
        Assert.True(pairIndex.IsUnique);

        var incident = model.FindEntityType(typeof(AvailabilityIncident))!;
        var incidentTable = StoreObjectIdentifier.Table("availability_incidents", null);
        Assert.Equal(typeof(Guid?), incident.FindProperty(nameof(AvailabilityIncident.AcknowledgedBy))!.ClrType);
        Assert.Equal(typeof(Guid?), incident.FindProperty(nameof(AvailabilityIncident.ResolvedBy))!.ClrType);
        var rowVersion = incident.FindProperty(nameof(AvailabilityIncident.RowVersion))!;
        Assert.Equal("bigint", rowVersion.GetRelationalTypeMapping().StoreType);
        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal(2_000, incident.FindProperty(nameof(AvailabilityIncident.AcknowledgementComment))!.GetMaxLength());
        Assert.Equal(2_000, incident.FindProperty(nameof(AvailabilityIncident.ResolutionNote))!.GetMaxLength());
        Assert.Equal("character varying(2000)", incident.FindProperty(nameof(AvailabilityIncident.AcknowledgementComment))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal("character varying(2000)", incident.FindProperty(nameof(AvailabilityIncident.ResolutionNote))!.GetRelationalTypeMapping().StoreType);
        Assert.Equal("acknowledged_by", incident.FindProperty(nameof(AvailabilityIncident.AcknowledgedBy))!.GetColumnName(incidentTable));
        Assert.Equal("resolved_by", incident.FindProperty(nameof(AvailabilityIncident.ResolvedBy))!.GetColumnName(incidentTable));

        var actorForeignKeys = incident.GetForeignKeys()
            .Where(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(HumanPrincipal))
            .ToArray();
        Assert.Equal(2, actorForeignKeys.Length);
        Assert.All(actorForeignKeys, foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        Assert.Contains(actorForeignKeys, foreignKey => foreignKey.Properties.Single().Name == nameof(AvailabilityIncident.AcknowledgedBy));
        Assert.Contains(actorForeignKeys, foreignKey => foreignKey.Properties.Single().Name == nameof(AvailabilityIncident.ResolvedBy));

        var checkConstraints = incident.GetCheckConstraints().ToArray();
        Assert.All(checkConstraints, constraint => Assert.NotNull(constraint.Name));
        var checks = checkConstraints.ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql);
        Assert.Contains("row_version > 0", checks["ck_availability_incidents_row_version"], StringComparison.Ordinal);
        Assert.Contains("confirmed-recovery", checks["ck_availability_incidents_resolution"], StringComparison.Ordinal);
        Assert.Contains("resolved_by IS NOT NULL", checks["ck_availability_incidents_status_lifecycle"], StringComparison.Ordinal);
        Assert.Contains("char_length(COALESCE(acknowledgement_comment, '')) BETWEEN 1 AND 2000", checks["ck_availability_incidents_lifecycle"], StringComparison.Ordinal);
        Assert.Contains("char_length(COALESCE(resolution_note, '')) BETWEEN 1 AND 2000", checks["ck_availability_incidents_resolution"], StringComparison.Ordinal);
        var activeIndex = Assert.Single(incident.GetIndexes(), index => index.GetDatabaseName() == "ux_availability_incidents_active_probe_rule");
        Assert.True(activeIndex.IsUnique);
        Assert.Equal("status IN ('Open', 'Acknowledged')", activeIndex.GetFilter());

        Assert.NotNull(model.FindEntityType(typeof(IncidentComment)));
        Assert.NotNull(model.FindEntityType(typeof(IncidentLifecycleAction)));
        Assert.NotNull(model.FindEntityType(typeof(IdempotencyReceipt)));

        var persisted = new HumanPrincipal(Guid.NewGuid(), "https://issuer.example.test", "synthetic-subject", DateTimeOffset.UtcNow);
        await using (var modified = new EePulseDbContext(options))
        {
            modified.Attach(persisted);
            modified.Entry(persisted).Property(principal => principal.Subject).CurrentValue = "modified-subject";
            await Assert.ThrowsAsync<InvalidOperationException>(() => modified.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        await using (var deleted = new EePulseDbContext(options))
        {
            deleted.Attach(persisted);
            deleted.Remove(persisted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => deleted.SaveChangesAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task HumanPrincipalImmutabilityTriggerTracksMigrationUpAndDown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = CreateOptions(postgres.ConnectionString);
        await using var db = new EePulseDbContext(options);

        await db.Database.MigrateAsync(ct);
        Assert.Contains(IncidentFoundationMigration, await db.Database.GetAppliedMigrationsAsync(ct));
        await AssertHumanPrincipalImmutabilityObjectsAsync(db, expected: true, ct);

        await db.GetService<IMigrator>().MigrateAsync(BaselineMigration, ct);
        Assert.DoesNotContain(IncidentFoundationMigration, await db.Database.GetAppliedMigrationsAsync(ct));
        await AssertHumanPrincipalImmutabilityObjectsAsync(db, expected: false, ct);
    }

    [Fact]
    public async Task CleanAndBaselineMigrationsPreserveSupportedWp06DataAndRejectUnknownActors()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = CreateOptions(postgres.ConnectionString);

        await using var migrationDb = new EePulseDbContext(options);
        await migrationDb.Database.MigrateAsync(ct);
        Assert.False(migrationDb.Database.HasPendingModelChanges());
        Assert.Contains(await migrationDb.Database.GetAppliedMigrationsAsync(ct), migration => migration == IncidentFoundationMigration);
        await AssertHumanPrincipalImmutabilityObjectsAsync(migrationDb, expected: true, ct);

        var migrator = migrationDb.GetService<IMigrator>();
        var rollbackSql = migrator.GenerateScript(IncidentFoundationMigration, BaselineMigration);
        Assert.Contains("system-policy", rollbackSql, StringComparison.Ordinal);
        Assert.Contains("::text", rollbackSql, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE", rollbackSql, StringComparison.OrdinalIgnoreCase);
        var dropPrincipalAt = rollbackSql.IndexOf("DROP TABLE", rollbackSql.IndexOf("human_principals", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        var dropActorConstraintAt = rollbackSql.IndexOf("DROP CONSTRAINT", StringComparison.OrdinalIgnoreCase);
        Assert.True(dropActorConstraintAt >= 0 && dropPrincipalAt > dropActorConstraintAt,
            "Down migration must remove incident dependencies before dropping the principal table.");
        var dropTriggerAt = rollbackSql.IndexOf($"DROP TRIGGER {HumanPrincipalImmutabilityTriggerName} ON public.human_principals", StringComparison.OrdinalIgnoreCase);
        var dropFunctionAt = rollbackSql.IndexOf($"DROP FUNCTION public.{HumanPrincipalImmutabilityFunctionName}()", StringComparison.OrdinalIgnoreCase);
        Assert.True(dropTriggerAt >= 0 && dropFunctionAt > dropTriggerAt && dropPrincipalAt > dropFunctionAt,
            "Down migration must drop the human-principal trigger, then its function, before dropping the table.");

        await migrator.MigrateAsync(BaselineMigration, ct);
        await AssertHumanPrincipalImmutabilityObjectsAsync(migrationDb, expected: false, ct);
        var inventory = await SeedInventoryAsync(migrationDb, 1, TestTime);
        var compatibility = await SeedBaselineLineageAsync(migrationDb, inventory, TestTime, ct);
        var openIncidentId = Guid.NewGuid();
        var resolvedIncidentId = Guid.NewGuid();
        await migrationDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at)
            VALUES ({openIncidentId}, {inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey}, {"Open"}, {TestTime});
            """, ct);
        await migrationDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note)
            VALUES ({resolvedIncidentId}, {inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey}, {"Resolved"}, {TestTime.AddMinutes(-2)}, {TestTime}, {"system-policy"}, {AvailabilityIncident.ConfirmedRecoveryReason});
            """, ct);

        var lifecycleEvent = IncidentLifecycleEvent.ForConfirmedRecovery(Guid.NewGuid(), resolvedIncidentId,
            inventory.ProbeIds[0], compatibility.AgentId, compatibility.ResultId, ProbeStatus.Up,
            compatibility.PolicyId, 1, TestTime);
        var suppression = NotificationSuppressionContext.ForConfirmedRecovery(lifecycleEvent, TestTime);
        migrationDb.AddRange(lifecycleEvent, suppression);
        await migrationDb.SaveChangesAsync(ct);

        await migrationDb.Database.MigrateAsync(ct);
        Assert.False(migrationDb.Database.HasPendingModelChanges());
        await AssertHumanPrincipalImmutabilityObjectsAsync(migrationDb, expected: true, ct);
        var open = await migrationDb.AvailabilityIncidents.AsNoTracking().SingleAsync(incident => incident.Id == openIncidentId, ct);
        var resolved = await migrationDb.AvailabilityIncidents.AsNoTracking().SingleAsync(incident => incident.Id == resolvedIncidentId, ct);
        Assert.Equal((AvailabilityIncidentStatus.Open, 1, 1L), (open.Status, open.OccurrenceCount, open.RowVersion));
        Assert.Equal((AvailabilityIncidentStatus.Resolved, 1, 1L, (Guid?)null, AvailabilityIncident.ConfirmedRecoveryReason),
            (resolved.Status, resolved.OccurrenceCount, resolved.RowVersion, resolved.ResolvedBy, resolved.ResolutionNote));
        Assert.Equal(0, await migrationDb.HumanPrincipals.CountAsync(ct));
        Assert.Equal(1, await migrationDb.IncidentLifecycleEvents.CountAsync(item => item.EventId == lifecycleEvent.EventId, ct));
        Assert.Equal(1, await migrationDb.NotificationSuppressionContexts.CountAsync(item => item.EventId == lifecycleEvent.EventId, ct));
        Assert.True(await migrationDb.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1
                FROM notification_suppression_contexts suppression
                JOIN incident_lifecycle_events lifecycle
                  ON lifecycle.event_id = suppression.event_id
                 AND lifecycle.incident_id = suppression.incident_id
                 AND lifecycle.lifecycle_event_key = suppression.lifecycle_event_key
                 AND lifecycle.policy_version = suppression.policy_version
                WHERE lifecycle.incident_id = {resolvedIncidentId}) AS "Value"
            """).SingleAsync(ct));
        Assert.Equal("Example Corp", (await migrationDb.Sites.AsNoTracking().SingleAsync(site => site.Id == inventory.SiteId, ct)).Name);
        Assert.Equal("Asia/Bangkok", (await migrationDb.UserTimezonePreferences.AsNoTracking()
            .SingleAsync(preference => preference.Issuer == compatibility.Issuer && preference.Subject == compatibility.Subject, ct)).Timezone);

        await migrator.MigrateAsync(BaselineMigration, ct);
        await AssertHumanPrincipalImmutabilityObjectsAsync(migrationDb, expected: false, ct);
        Assert.Equal("Open", await migrationDb.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM availability_incidents WHERE id = {openIncidentId}
            """).SingleAsync(ct));
        Assert.Equal(TestTime, await migrationDb.Database.SqlQuery<DateTimeOffset>($"""
            SELECT opened_at AS "Value" FROM availability_incidents WHERE id = {openIncidentId}
            """).SingleAsync(ct));
        Assert.Equal(("Resolved", TestTime.AddMinutes(-2), TestTime, "system-policy", AvailabilityIncident.ConfirmedRecoveryReason, 1),
            (
                await migrationDb.Database.SqlQuery<string>($"""
                    SELECT status AS "Value" FROM availability_incidents WHERE id = {resolvedIncidentId}
                    """).SingleAsync(ct),
                await migrationDb.Database.SqlQuery<DateTimeOffset>($"""
                    SELECT opened_at AS "Value" FROM availability_incidents WHERE id = {resolvedIncidentId}
                    """).SingleAsync(ct),
                await migrationDb.Database.SqlQuery<DateTimeOffset>($"""
                    SELECT resolved_at AS "Value" FROM availability_incidents WHERE id = {resolvedIncidentId}
                    """).SingleAsync(ct),
                await migrationDb.Database.SqlQuery<string>($"""
                    SELECT resolved_by AS "Value" FROM availability_incidents WHERE id = {resolvedIncidentId}
                    """).SingleAsync(ct),
                await migrationDb.Database.SqlQuery<string>($"""
                    SELECT resolution_note AS "Value" FROM availability_incidents WHERE id = {resolvedIncidentId}
                    """).SingleAsync(ct),
                await migrationDb.Database.SqlQuery<int>($"""
                    SELECT occurrence_count AS "Value" FROM availability_incidents WHERE id = {resolvedIncidentId}
                    """).SingleAsync(ct)));

        await migrator.MigrateAsync(null, ct);
        await AssertHumanPrincipalImmutabilityObjectsAsync(migrationDb, expected: true, ct);
        var reupgradedOpen = await migrationDb.AvailabilityIncidents.AsNoTracking().SingleAsync(incident => incident.Id == openIncidentId, ct);
        var reupgradedResolved = await migrationDb.AvailabilityIncidents.AsNoTracking().SingleAsync(incident => incident.Id == resolvedIncidentId, ct);
        Assert.Equal((AvailabilityIncidentStatus.Open, 1, 1L), (reupgradedOpen.Status, reupgradedOpen.OccurrenceCount, reupgradedOpen.RowVersion));
        Assert.Equal((AvailabilityIncidentStatus.Resolved, 1, 1L, (Guid?)null, AvailabilityIncident.ConfirmedRecoveryReason),
            (reupgradedResolved.Status, reupgradedResolved.OccurrenceCount, reupgradedResolved.RowVersion,
                reupgradedResolved.ResolvedBy, reupgradedResolved.ResolutionNote));

        await migrator.MigrateAsync(BaselineMigration, ct);
        const string unsupportedActor = "synthetic-unsupported-legacy-actor";
        await migrationDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET resolved_by = {unsupportedActor} WHERE id = {resolvedIncidentId}", ct);
        var rejectedUpgrade = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(null, ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rejectedUpgrade.SqlState);
        Assert.Equal("WP07 incident identity migration found unsupported legacy actor data.", rejectedUpgrade.MessageText);
        Assert.DoesNotContain(unsupportedActor, rejectedUpgrade.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(IncidentFoundationMigration, await migrationDb.Database.GetAppliedMigrationsAsync(ct));
        await AssertHumanPrincipalImmutabilityObjectsAsync(migrationDb, expected: false, ct);
    }

    [Fact]
    public async Task ConcurrentActorWriteFailsFastBeforeDowngradeGuardRuns()
    {
        using var preparationTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        preparationTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        var preparationToken = preparationTimeout.Token;
        await using var fixture = await CreatePersistenceFixtureAsync(1, preparationToken);
        var principalId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        const string issuer = "https://issuer.example.test/concurrent-actor-downgrade";
        const string subject = "synthetic-concurrent-actor-downgrade-subject";
        const string acknowledgementComment = "concurrent actor downgrade";
        const string resolutionNote = "concurrent actor resolution";

        await using var writer = new NpgsqlConnection(fixture.Database.ConnectionString);
        await using var migrationDb = new EePulseDbContext(fixture.Options);
        await writer.OpenAsync(preparationToken);
        await migrationDb.Database.OpenConnectionAsync(preparationToken);
        await using var writerTransaction = await writer.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, preparationToken);

        Task? migrationTask = null;
        var migrationObserved = false;
        var writerCommitted = false;
        ExceptionDispatchInfo? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        using var behaviorTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        behaviorTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using (var childLock = new NpgsqlCommand("LOCK TABLE availability_incidents IN ROW EXCLUSIVE MODE", writer, writerTransaction))
            {
                await childLock.ExecuteNonQueryAsync(preparationToken);
            }

            await using (var principalInsert = new NpgsqlCommand("""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES (@id, @issuer, @subject, @created_at)
                """, writer, writerTransaction))
            {
                principalInsert.Parameters.AddWithValue("id", principalId);
                principalInsert.Parameters.AddWithValue("issuer", issuer);
                principalInsert.Parameters.AddWithValue("subject", subject);
                principalInsert.Parameters.AddWithValue("created_at", TestTime);
                await principalInsert.ExecuteNonQueryAsync(preparationToken);
            }

            await using (var incidentInsert = new NpgsqlCommand("""
                INSERT INTO availability_incidents
                    (id, probe_id, rule_key, status, opened_at, acknowledged_at, acknowledged_by,
                     acknowledgement_comment, resolved_at, resolved_by, resolution_note, occurrence_count)
                VALUES
                    (@id, @probe_id, @rule_key, 'Resolved', @opened_at, @acknowledged_at, @acknowledged_by,
                     @acknowledgement_comment, @resolved_at, @resolved_by, @resolution_note, 1)
                """, writer, writerTransaction))
            {
                incidentInsert.Parameters.AddWithValue("id", incidentId);
                incidentInsert.Parameters.AddWithValue("probe_id", fixture.Inventory.ProbeIds[0]);
                incidentInsert.Parameters.AddWithValue("rule_key", AvailabilityIncident.AvailabilityDownRuleKey);
                incidentInsert.Parameters.AddWithValue("opened_at", TestTime);
                incidentInsert.Parameters.AddWithValue("acknowledged_at", TestTime);
                incidentInsert.Parameters.AddWithValue("acknowledged_by", principalId);
                incidentInsert.Parameters.AddWithValue("acknowledgement_comment", acknowledgementComment);
                incidentInsert.Parameters.AddWithValue("resolved_at", TestTime.AddMinutes(1));
                incidentInsert.Parameters.AddWithValue("resolved_by", principalId);
                incidentInsert.Parameters.AddWithValue("resolution_note", resolutionNote);
                await incidentInsert.ExecuteNonQueryAsync(preparationToken);
            }

            migrationTask = migrationDb.GetService<IMigrator>().MigrateAsync(BaselineMigration, behaviorTimeout.Token);
            InvalidOperationException lockFailure;
            try
            {
                lockFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => migrationTask!.WaitAsync(behaviorTimeout.Token));
            }
            finally
            {
                migrationObserved = migrationTask.IsCompleted;
            }

            var lockError = Assert.IsType<PostgresException>(lockFailure.InnerException);
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, lockError.SqlState);
            Assert.Contains("availability_incidents", lockError.MessageText, StringComparison.Ordinal);

            await writerTransaction.CommitAsync(behaviorTimeout.Token);
            writerCommitted = true;

            await using (var retry = new EePulseDbContext(fixture.Options))
            {
                var guardError = await Assert.ThrowsAsync<PostgresException>(() =>
                    retry.GetService<IMigrator>().MigrateAsync(BaselineMigration, behaviorTimeout.Token));
                Assert.Equal(PostgresErrorCodes.CheckViolation, guardError.SqlState);
                Assert.Equal("WP07 incident identity downgrade requires empty principal and actor data.", guardError.MessageText);
            }

            await using var verify = new EePulseDbContext(fixture.Options);
            await AssertCurrentDowngradeSchemaAsync(verify, preparationToken);
            await AssertHumanPrincipalPreservedAsync(verify, principalId, issuer, subject, preparationToken);
            await AssertIncidentPreservedAsync(verify, incidentId, fixture.Inventory.ProbeIds[0], principalId,
                acknowledgementComment, resolutionNote, preparationToken);
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            await CleanupDowngradeConcurrencyAsync(writerTransaction, writerCommitted, migrationTask,
                migrationObserved, behaviorTimeout, cleanupFailures);
        }

        ThrowPrimaryOrCleanupFailure(primaryFailure, cleanupFailures, "Concurrent actor downgrade cleanup failed.", "ConcurrentActorDowngradeCleanupFailure");
    }

    [Fact]
    public async Task ConcurrentPrincipalWriteIsObservedBeforeDowngradeGuardRuns()
    {
        using var preparationTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        preparationTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        var preparationToken = preparationTimeout.Token;
        await using var fixture = await CreatePersistenceFixtureAsync(1, preparationToken);
        var principalId = Guid.NewGuid();
        const string issuer = "https://issuer.example.test/concurrent-principal-downgrade";
        const string subject = "synthetic-concurrent-principal-downgrade-subject";

        await using var writer = new NpgsqlConnection(fixture.Database.ConnectionString);
        await using var migrationDb = new EePulseDbContext(fixture.Options);
        await writer.OpenAsync(preparationToken);
        await migrationDb.Database.OpenConnectionAsync(preparationToken);
        await using var writerTransaction = await writer.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, preparationToken);

        Task? migrationTask = null;
        var migrationObserved = false;
        var writerCommitted = false;
        ExceptionDispatchInfo? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        using var behaviorTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        behaviorTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using (var principalInsert = new NpgsqlCommand("""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES (@id, @issuer, @subject, @created_at)
                """, writer, writerTransaction))
            {
                principalInsert.Parameters.AddWithValue("id", principalId);
                principalInsert.Parameters.AddWithValue("issuer", issuer);
                principalInsert.Parameters.AddWithValue("subject", subject);
                principalInsert.Parameters.AddWithValue("created_at", TestTime);
                await principalInsert.ExecuteNonQueryAsync(preparationToken);
            }

            migrationTask = migrationDb.GetService<IMigrator>().MigrateAsync(BaselineMigration, behaviorTimeout.Token);
            InvalidOperationException lockFailure;
            try
            {
                lockFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => migrationTask!.WaitAsync(behaviorTimeout.Token));
            }
            finally
            {
                migrationObserved = migrationTask.IsCompleted;
            }

            var lockError = Assert.IsType<PostgresException>(lockFailure.InnerException);
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, lockError.SqlState);
            Assert.Contains("human_principals", lockError.MessageText, StringComparison.Ordinal);

            await writerTransaction.CommitAsync(behaviorTimeout.Token);
            writerCommitted = true;

            await using (var retry = new EePulseDbContext(fixture.Options))
            {
                var guardError = await Assert.ThrowsAsync<PostgresException>(() =>
                    retry.GetService<IMigrator>().MigrateAsync(BaselineMigration, behaviorTimeout.Token));
                Assert.Equal(PostgresErrorCodes.CheckViolation, guardError.SqlState);
                Assert.Equal("WP07 incident identity downgrade requires empty principal and actor data.", guardError.MessageText);
            }

            await using var verify = new EePulseDbContext(fixture.Options);
            await AssertCurrentDowngradeSchemaAsync(verify, preparationToken);
            await AssertHumanPrincipalPreservedAsync(verify, principalId, issuer, subject, preparationToken);
            Assert.Equal(0, await verify.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM availability_incidents
                """).SingleAsync(preparationToken));
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            await CleanupDowngradeConcurrencyAsync(writerTransaction, writerCommitted, migrationTask,
                migrationObserved, behaviorTimeout, cleanupFailures);
        }

        ThrowPrimaryOrCleanupFailure(primaryFailure, cleanupFailures, "Concurrent principal downgrade cleanup failed.", "ConcurrentPrincipalDowngradeCleanupFailure");
    }

    [Fact]
    public async Task PopulatedPrincipalDowngradeFailsClosedWithoutLosingCurrentSchemaData()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        var principalId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        const string issuer = "https://issuer.example.test/downgrade";
        const string subject = "synthetic-downgrade-subject";
        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({principalId}, {issuer}, {subject}, {TestTime});
                INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, acknowledged_at, acknowledged_by, acknowledgement_comment, resolved_at, resolved_by, resolution_note)
                VALUES ({incidentId}, {fixture.Inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey}, {"Resolved"}, {TestTime}, {TestTime}, {principalId}, {"downgrade guard actor"}, {TestTime.AddMinutes(1)}, {principalId}, {"downgrade guard resolution"});
                """, ct);
        }

        await using (var migrate = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                migrate.GetService<IMigrator>().MigrateAsync(BaselineMigration, ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("WP07 incident identity downgrade requires empty principal and actor data.", error.MessageText);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Contains(IncidentFoundationMigration, await verify.Database.GetAppliedMigrationsAsync(ct));
        Assert.Equal(issuer, await verify.Database.SqlQuery<string>($"""
            SELECT issuer AS "Value" FROM human_principals WHERE id = {principalId}
            """).SingleAsync(ct));
        Assert.Equal(subject, await verify.Database.SqlQuery<string>($"""
            SELECT subject AS "Value" FROM human_principals WHERE id = {principalId}
            """).SingleAsync(ct));
        Assert.Equal(principalId, await verify.Database.SqlQuery<Guid?>($"""
            SELECT acknowledged_by AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(principalId, await verify.Database.SqlQuery<Guid?>($"""
            SELECT resolved_by AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(1L, await verify.Database.SqlQuery<long>($"""
            SELECT row_version AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(3, await verify.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = 'availability_incidents'
              AND ((column_name = 'acknowledged_by' AND udt_name = 'uuid')
                OR (column_name = 'resolved_by' AND udt_name = 'uuid')
                OR (column_name = 'row_version' AND udt_name = 'int8'))
            """).SingleAsync(ct));
        Assert.True(await verify.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND tablename = 'human_principals'
                  AND indexname = 'ux_human_principals_issuer_subject') AS "Value"
            """).SingleAsync(ct));
        Assert.Equal(6, await verify.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM pg_constraint
            WHERE conrelid IN ('availability_incidents'::regclass, 'human_principals'::regclass)
              AND conname IN (
                  'ck_availability_incidents_lifecycle',
                  'ck_availability_incidents_resolution',
                  'ck_availability_incidents_row_version',
                  'ck_availability_incidents_status_lifecycle',
                  'fk_availability_incidents_human_principals_acknowledged_by',
                  'fk_availability_incidents_human_principals_resolved_by')
            """).SingleAsync(ct));
        Assert.Equal(2, await verify.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM pg_constraint
            WHERE conrelid = 'human_principals'::regclass
              AND conname IN ('ck_human_principals_issuer_shape', 'ck_human_principals_subject_shape')
            """).SingleAsync(ct));
    }

    [Fact]
    public async Task NontrivialRowVersionDowngradeFailsClosedWithoutLosingCurrentSchemaData()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        var incidentId = Guid.NewGuid();
        const int occurrenceCount = 4;
        const long rowVersion = 2;

        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO availability_incidents
                    (id, probe_id, rule_key, status, opened_at, resolved_at, resolution_note, occurrence_count, row_version)
                VALUES
                    ({incidentId}, {fixture.Inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey},
                     {"Resolved"}, {TestTime}, {TestTime.AddMinutes(1)}, {AvailabilityIncident.ConfirmedRecoveryReason},
                     {occurrenceCount}, {rowVersion})
                """, ct);
            Assert.Equal(0, await seed.HumanPrincipals.CountAsync(ct));
        }

        await using (var migrate = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                migrate.GetService<IMigrator>().MigrateAsync(BaselineMigration, ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("WP07 incident row-version downgrade requires every incident row_version to equal 1.", error.MessageText);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        await AssertCurrentDowngradeSchemaAsync(verify, ct);
        Assert.Contains(IncidentFoundationMigration, await verify.Database.GetAppliedMigrationsAsync(ct));
        Assert.Equal(0, await verify.HumanPrincipals.CountAsync(ct));
        await AssertAutomaticIncidentPreservedAsync(verify, incidentId, fixture.Inventory.ProbeIds[0], occurrenceCount, rowVersion, ct);
    }

    [Fact]
    public async Task LongIncidentTextDowngradeFailsClosedBeforeLegacyColumnConversion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        var principalId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        const string issuer = "https://issuer.example.test/long-text-downgrade";
        const string subject = "synthetic-long-text-downgrade-subject";
        var acknowledgementComment = new string('a', 1_001);
        var resolutionNote = new string('r', 1_001);

        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({principalId}, {issuer}, {subject}, {TestTime});
                INSERT INTO availability_incidents
                    (id, probe_id, rule_key, status, opened_at, acknowledged_at, acknowledged_by,
                     acknowledgement_comment, resolved_at, resolved_by, resolution_note, occurrence_count)
                VALUES
                    ({incidentId}, {fixture.Inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey},
                     {"Resolved"}, {TestTime}, {TestTime}, {principalId}, {acknowledgementComment},
                     {TestTime.AddMinutes(1)}, {principalId}, {resolutionNote}, 1)
                """, ct);
        }

        await using (var migrate = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                migrate.GetService<IMigrator>().MigrateAsync(BaselineMigration, ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("WP07 incident text downgrade requires acknowledgement_comment and resolution_note to be at most 1000 characters.", error.MessageText);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        await AssertCurrentDowngradeSchemaAsync(verify, ct);
        Assert.Equal(1, await verify.HumanPrincipals.CountAsync(ct));
        await AssertHumanPrincipalPreservedAsync(verify, principalId, issuer, subject, ct);
        await AssertIncidentPreservedAsync(verify, incidentId, fixture.Inventory.ProbeIds[0], principalId,
            acknowledgementComment, resolutionNote, ct);
    }

    [Fact]
    public async Task HumanPrincipalPersistencePreservesExactPairAndRejectsDuplicatesAndInvalidWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        var issuer = "https://issuer.example.test/ExactCase";
        var subject = "Synthetic-Subject-01";
        var principalId = Guid.Parse("12d9d201-a30f-4b5f-ae1d-b617ad4425e1");
        await using (var insert = new EePulseDbContext(fixture.Options))
        {
            insert.HumanPrincipals.Add(new HumanPrincipal(principalId, issuer, subject, TestTime));
            await insert.SaveChangesAsync(ct);
        }

        await using (var lookup = new EePulseDbContext(fixture.Options))
        {
            var exactPair = await lookup.HumanPrincipals.AsNoTracking()
                .SingleAsync(principal => principal.Issuer == issuer && principal.Subject == subject, ct);
            Assert.Equal(principalId, exactPair.Id);
            Assert.Equal((issuer, subject), (exactPair.Issuer, exactPair.Subject));
        }

        await using (var duplicate = new EePulseDbContext(fixture.Options))
        {
            duplicate.HumanPrincipals.Add(new HumanPrincipal(Guid.NewGuid(), issuer, subject, TestTime));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(ct));
            Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)error.InnerException!).SqlState);
            Assert.DoesNotContain(issuer, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(subject, error.ToString(), StringComparison.Ordinal);
        }

        var caseAndComponentDistinct = new[]
        {
            new HumanPrincipal(Guid.NewGuid(), issuer, "synthetic-subject-01", TestTime),
            new HumanPrincipal(Guid.NewGuid(), "https://issuer.example.test/exactcase", subject, TestTime),
            new HumanPrincipal(Guid.NewGuid(), issuer, "Synthetic-Subject-02", TestTime),
            new HumanPrincipal(Guid.NewGuid(), "https://other-issuer.example.test/ExactCase", subject, TestTime),
        };
        await using (var distinct = new EePulseDbContext(fixture.Options))
        {
            distinct.HumanPrincipals.AddRange(caseAndComponentDistinct);
            await distinct.SaveChangesAsync(ct);
            Assert.Equal(caseAndComponentDistinct.Length, await distinct.HumanPrincipals.AsNoTracking()
                .CountAsync(principal => principal.Id != principalId, ct));
        }

        await using (var invalidShape = new EePulseDbContext(fixture.Options))
        {
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => invalidShape.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({Guid.NewGuid()}, {" synthetic-issuer "}, {"synthetic-subject"}, {TestTime})
                """, ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        }

        await using (var overlength = new EePulseDbContext(fixture.Options))
        {
            var tooLong = new string('i', HumanPrincipal.MaximumIssuerLength + 1);
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => overlength.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({Guid.NewGuid()}, {tooLong}, {"synthetic-subject"}, {TestTime})
                """, ct));
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, invalid.SqlState);
        }
    }

    [Fact]
    public async Task HumanPrincipalDatabaseTriggerRejectsDirectIdentityRekey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        await using (var migrationShape = new EePulseDbContext(fixture.Options))
        {
            await AssertHumanPrincipalImmutabilityObjectsAsync(migrationShape, expected: true, ct);
        }

        var seed = await SeedHumanPrincipalWithAcknowledgedIncidentAsync(fixture, ct);
        const string rekeyedIssuer = "https://issuer.example.test/rekeyed";
        const string rekeyedSubject = "synthetic-rekeyed-identity";

        await using (var before = new EePulseDbContext(fixture.Options))
        {
            Assert.Equal(1, await before.HumanPrincipals.AsNoTracking().CountAsync(ct));
            Assert.Equal(0, await before.HumanPrincipals.AsNoTracking()
                .CountAsync(principal => principal.Issuer == rekeyedIssuer && principal.Subject == rekeyedSubject, ct));
        }

        await using (var update = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => update.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE human_principals
                SET issuer = {rekeyedIssuer}, subject = {rekeyedSubject}
                WHERE id = {seed.PrincipalId}
                """, ct));
            AssertHumanPrincipalMutationRejected(error);
        }

        var verifiedAfterRejectedUpdate = await AssertHumanPrincipalAndAcknowledgedActorUnchangedAsync(fixture, seed, ct);
        Assert.Equal(1, verifiedAfterRejectedUpdate);

        var insertId = Guid.NewGuid();
        const string insertedIssuer = "https://issuer.example.test/direct-insert";
        const string insertedSubject = "synthetic-direct-insert";
        var insertedAt = TestTime.AddMinutes(1);
        await using (var insert = new EePulseDbContext(fixture.Options))
        {
            await insert.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({insertId}, {insertedIssuer}, {insertedSubject}, {insertedAt})
                """, ct);
        }

        await using var verifyInsert = new EePulseDbContext(fixture.Options);
        Assert.Equal(2, await verifyInsert.HumanPrincipals.AsNoTracking().CountAsync(ct));
        var inserted = await verifyInsert.HumanPrincipals.AsNoTracking().SingleAsync(principal => principal.Id == insertId, ct);
        Assert.Equal((insertId, insertedIssuer, insertedSubject, insertedAt),
            (inserted.Id, inserted.Issuer, inserted.Subject, inserted.CreatedAt));
    }

    [Fact]
    public async Task HumanPrincipalDatabaseTriggerRejectsDirectDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        await using (var migrationShape = new EePulseDbContext(fixture.Options))
        {
            await AssertHumanPrincipalImmutabilityObjectsAsync(migrationShape, expected: true, ct);
        }

        var seed = await SeedHumanPrincipalWithAcknowledgedIncidentAsync(fixture, ct);
        await using (var delete = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => delete.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM human_principals WHERE id = {seed.PrincipalId}
                """, ct));
            AssertHumanPrincipalMutationRejected(error);
        }

        var verifiedAfterRejectedDelete = await AssertHumanPrincipalAndAcknowledgedActorUnchangedAsync(fixture, seed, ct);
        Assert.Equal(1, verifiedAfterRejectedDelete);
    }

    [Fact]
    public async Task HumanPrincipalSqlConstraintsRejectLineAndParagraphSeparators()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        var invalidValues = new[]
        {
            ("https://issuer.example.test/embedded\u2028separator", "synthetic-subject"),
            ("https://issuer.example.test/embedded\u2029separator", "synthetic-subject"),
            ("https://issuer.example.test", "synthetic\u2028subject"),
            ("https://issuer.example.test", "synthetic\u2029subject"),
        };

        foreach (var (issuer, subject) in invalidValues)
        {
            await using var invalidWrite = new EePulseDbContext(fixture.Options);
            var error = await Assert.ThrowsAsync<PostgresException>(() => invalidWrite.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({Guid.NewGuid()}, {issuer}, {subject}, {TestTime})
                """, ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task HumanPrincipalSqlConstraintsRejectC1ControlsInIssuerAndSubject()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        var validId = Guid.NewGuid();
        const string validIssuer = "https://issuer.example.test/valid\u00A1value";
        const string validSubject = "synthetic-valid\u00A1subject";

        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({validId}, {validIssuer}, {validSubject}, {TestTime})
                """, ct);
        }

        int persistedCountBefore;
        await using (var before = new EePulseDbContext(fixture.Options))
        {
            persistedCountBefore = await before.HumanPrincipals.AsNoTracking().CountAsync(ct);
        }
        Assert.Equal(1, persistedCountBefore);
        var attemptedIds = new List<Guid>();
        var columns = new[] { "issuer", "subject" };

        foreach (var codePoint in Enumerable.Range(0x80, 0x20))
        {
            var control = char.ConvertFromUtf32(codePoint);
            foreach (var column in columns)
            {
                var id = Guid.NewGuid();
                attemptedIds.Add(id);
                var invalidValue = $"before{control}after";
                var issuer = column == "issuer" ? invalidValue : validIssuer;
                var subject = column == "subject" ? invalidValue : validSubject;

                await using var invalidWrite = new EePulseDbContext(fixture.Options);
                var error = await Assert.ThrowsAsync<PostgresException>(() => invalidWrite.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO human_principals (id, issuer, subject, created_at)
                    VALUES ({id}, {issuer}, {subject}, {TestTime})
                    """, ct));

                Assert.True(error.SqlState == PostgresErrorCodes.CheckViolation,
                    $"Expected SQLSTATE 23514 for U+{codePoint:X4} in {column}, received {error.SqlState}.");
                var expectedConstraint = $"ck_human_principals_{column}_shape";
                Assert.True(error.ConstraintName == expectedConstraint,
                    $"Expected constraint {expectedConstraint} for U+{codePoint:X4} in {column}, received {error.ConstraintName}.");
            }
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(persistedCountBefore, await verify.HumanPrincipals.AsNoTracking().CountAsync(ct));
        Assert.Empty(await verify.HumanPrincipals.AsNoTracking()
            .Where(principal => attemptedIds.Contains(principal.Id))
            .ToListAsync(ct));
        var persistedValid = await verify.HumanPrincipals.AsNoTracking().SingleAsync(principal => principal.Id == validId, ct);
        Assert.Equal(validIssuer, persistedValid.Issuer);
        Assert.Equal(validSubject, persistedValid.Subject);
    }

    [Fact]
    public async Task HumanPrincipalSqlConstraintsUsePostgreSqlCharacterLengthForAstralBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        var issuer512 = string.Concat(Enumerable.Repeat("\U0001F600", 512));
        var subject512 = string.Concat(Enumerable.Repeat("\U0001F680", 512));
        var validId = Guid.NewGuid();

        await using (var insert = new EePulseDbContext(fixture.Options))
        {
            await insert.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({validId}, {issuer512}, {subject512}, {TestTime})
                """, ct);
        }

        await using (var materialized = new EePulseDbContext(fixture.Options))
        {
            var principal = await materialized.HumanPrincipals.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == validId, ct);
            Assert.Equal(issuer512, principal.Issuer);
            Assert.Equal(subject512, principal.Subject);
            Assert.Equal(512, principal.Issuer.EnumerateRunes().Count());
            Assert.Equal(512, principal.Subject.EnumerateRunes().Count());
        }

        var issuer513 = string.Concat(Enumerable.Repeat("\U0001F600", 513));
        await using (var tooLongIssuer = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => tooLongIssuer.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({Guid.NewGuid()}, {issuer513}, {subject512}, {TestTime})
                """, ct));
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, error.SqlState);
        }

        var subject513 = string.Concat(Enumerable.Repeat("\U0001F680", 513));
        await using (var tooLongSubject = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => tooLongSubject.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO human_principals (id, issuer, subject, created_at)
                VALUES ({Guid.NewGuid()}, {issuer512}, {subject513}, {TestTime})
                """, ct));
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, error.SqlState);
        }
    }

    [Fact]
    public async Task IncidentActorAndTwoThousandCharacterConstraintsAreEnforcedByPostgreSql()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(1, ct);
        var principalId = Guid.NewGuid();
        const string principalIssuer = "https://issuer.example.test";
        const string principalSubject = "human-01";
        await using (var principal = new EePulseDbContext(fixture.Options))
        {
            principal.HumanPrincipals.Add(new HumanPrincipal(principalId, principalIssuer, principalSubject, TestTime));
            await principal.SaveChangesAsync(ct);
        }

        var incidentId = Guid.NewGuid();
        var unknownPrincipalId = Guid.NewGuid();
        await using (var unknownActor = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => unknownActor.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, acknowledged_at, acknowledged_by, acknowledgement_comment)
                VALUES ({Guid.NewGuid()}, {fixture.Inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey}, {"Acknowledged"}, {TestTime}, {TestTime}, {unknownPrincipalId}, {"synthetic investigation"})
                """, ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        var comment = new string('c', 2_000);
        await using (var validHumanActor = new EePulseDbContext(fixture.Options))
        {
            await validHumanActor.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, acknowledged_at, acknowledged_by, acknowledgement_comment)
                VALUES ({incidentId}, {fixture.Inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey}, {"Acknowledged"}, {TestTime}, {TestTime}, {principalId}, {comment})
                """, ct);
            var emptyComment = await Assert.ThrowsAsync<PostgresException>(() => validHumanActor.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET acknowledgement_comment = {string.Empty} WHERE id = {incidentId}", ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, emptyComment.SqlState);
            var overlength = await Assert.ThrowsAsync<PostgresException>(() => validHumanActor.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET acknowledgement_comment = {comment + "x"} WHERE id = {incidentId}", ct));
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, overlength.SqlState);
            await validHumanActor.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE availability_incidents
                SET status = {"Resolved"}, resolved_at = {TestTime.AddMinutes(1)}, resolved_by = {principalId}, resolution_note = {comment}
                WHERE id = {incidentId}
                """, ct);
            Assert.Equal(2_000, await validHumanActor.Database.SqlQuery<int>($"SELECT char_length(resolution_note) AS \"Value\" FROM availability_incidents WHERE id = {incidentId}").SingleAsync(ct));
            var emptyResolutionNote = await Assert.ThrowsAsync<PostgresException>(() => validHumanActor.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET resolution_note = {string.Empty} WHERE id = {incidentId}", ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, emptyResolutionNote.SqlState);
            var resolutionOverlength = await Assert.ThrowsAsync<PostgresException>(() => validHumanActor.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET resolution_note = {comment + "x"} WHERE id = {incidentId}", ct));
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, resolutionOverlength.SqlState);

            var restrictForeignKeyCount = await validHumanActor.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM pg_constraint
                WHERE conrelid = 'public.availability_incidents'::regclass
                  AND contype = 'f'
                  AND confdeltype = 'r'
                  AND conname IN (
                      'fk_availability_incidents_human_principals_acknowledged_by',
                      'fk_availability_incidents_human_principals_resolved_by')
                """).SingleAsync(ct);
            Assert.Equal(2, restrictForeignKeyCount);

            var restrictiveDelete = await Assert.ThrowsAsync<PostgresException>(() => validHumanActor.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM human_principals WHERE id = {principalId}", ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, restrictiveDelete.SqlState);
            Assert.Equal("WP07 human principals are immutable.", restrictiveDelete.MessageText);
            Assert.Equal("tr_human_principals_immutable", restrictiveDelete.ConstraintName);
            Assert.DoesNotContain(principalIssuer, restrictiveDelete.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(principalSubject, restrictiveDelete.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(principalId.ToString("D"), restrictiveDelete.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(incidentId.ToString("D"), restrictiveDelete.ToString(), StringComparison.Ordinal);
        }

        await using (var verify = new EePulseDbContext(fixture.Options))
        {
            Assert.Equal(1, await verify.HumanPrincipals.AsNoTracking().CountAsync(ct));
            Assert.Equal(1, await verify.AvailabilityIncidents.AsNoTracking().CountAsync(ct));
            var principal = await verify.HumanPrincipals.AsNoTracking().SingleAsync(candidate => candidate.Id == principalId, ct);
            Assert.Equal((principalId, principalIssuer, principalSubject, TestTime),
                (principal.Id, principal.Issuer, principal.Subject, principal.CreatedAt));
            var incident = await verify.AvailabilityIncidents.AsNoTracking().SingleAsync(candidate => candidate.Id == incidentId, ct);
            Assert.Equal((AvailabilityIncidentStatus.Resolved, principalId, principalId),
                (incident.Status, incident.AcknowledgedBy, incident.ResolvedBy));
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM availability_incidents AS incident
                JOIN human_principals AS acknowledged_principal ON acknowledged_principal.id = incident.acknowledged_by
                JOIN human_principals AS resolved_principal ON resolved_principal.id = incident.resolved_by
                WHERE incident.id = {incidentId}
                  AND acknowledged_principal.id = {principalId}
                  AND resolved_principal.id = {principalId}
                  AND acknowledged_principal.issuer = {principalIssuer}
                  AND acknowledged_principal.subject = {principalSubject}
                  AND resolved_principal.issuer = {principalIssuer}
                  AND resolved_principal.subject = {principalSubject}
                """).SingleAsync(ct));
        }

        await using (var unknownResolvedActor = new EePulseDbContext(fixture.Options))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => unknownResolvedActor.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note)
                VALUES ({Guid.NewGuid()}, {fixture.Inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey}, {"Resolved"}, {TestTime}, {TestTime}, {unknownPrincipalId}, {"synthetic manual resolution"})
                """, ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task IncidentVersionsAdvanceExactlyOnceAndStaleOrRolledBackWritesDoNotPersist()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreatePersistenceFixtureAsync(2, ct);
        var incidentId = Guid.NewGuid();
        var openedAt = TestTime.AddMinutes(-1);
        await using (var create = new EePulseDbContext(fixture.Options))
        {
            create.AvailabilityIncidents.Add(new AvailabilityIncident(incidentId, fixture.Inventory.ProbeIds[0], openedAt));
            await create.SaveChangesAsync(ct);
            Assert.Equal(1L, create.AvailabilityIncidents.Local.Single().RowVersion);
        }

        await using var winner = new EePulseDbContext(fixture.Options);
        await using var stale = new EePulseDbContext(fixture.Options);
        var firstCopy = await winner.AvailabilityIncidents.SingleAsync(incident => incident.Id == incidentId, ct);
        var staleCopy = await stale.AvailabilityIncidents.SingleAsync(incident => incident.Id == incidentId, ct);
        firstCopy.RecordRecoveryFailedOccurrence();
        await winner.SaveChangesAsync(ct);
        Assert.Equal(2L, firstCopy.RowVersion);

        staleCopy.ResolveForConfirmedRecovery(TestTime);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(ct));
        await using (var verify = new EePulseDbContext(fixture.Options))
        {
            var stored = await verify.AvailabilityIncidents.AsNoTracking().SingleAsync(incident => incident.Id == incidentId, ct);
            Assert.Equal((AvailabilityIncidentStatus.Open, 2, 2L, (DateTimeOffset?)null, (Guid?)null, (string?)null),
                (stored.Status, stored.OccurrenceCount, stored.RowVersion, stored.ResolvedAt, stored.ResolvedBy, stored.ResolutionNote));
        }

        await using (var rolledBack = new EePulseDbContext(fixture.Options))
        {
            await using var transaction = await rolledBack.Database.BeginTransactionAsync(ct);
            var incident = await rolledBack.AvailabilityIncidents.SingleAsync(candidate => candidate.Id == incidentId, ct);
            incident.RecordRecoveryFailedOccurrence();
            await rolledBack.SaveChangesAsync(ct);
            Assert.Equal(3L, incident.RowVersion);
            await transaction.RollbackAsync(ct);
        }
        await using (var verifyRollback = new EePulseDbContext(fixture.Options))
        {
            var stored = await verifyRollback.AvailabilityIncidents.AsNoTracking().SingleAsync(incident => incident.Id == incidentId, ct);
            Assert.Equal((AvailabilityIncidentStatus.Open, 2, 2L), (stored.Status, stored.OccurrenceCount, stored.RowVersion));
        }

        var resolvedId = Guid.NewGuid();
        await using (var resolve = new EePulseDbContext(fixture.Options))
        {
            var incident = new AvailabilityIncident(resolvedId, fixture.Inventory.ProbeIds[1], openedAt);
            resolve.AvailabilityIncidents.Add(incident);
            await resolve.SaveChangesAsync(ct);
            incident.ResolveForConfirmedRecovery(TestTime);
            await resolve.SaveChangesAsync(ct);
            Assert.Equal(2L, incident.RowVersion);
            Assert.Null(incident.ResolvedBy);
            Assert.Equal(AvailabilityIncident.ConfirmedRecoveryReason, incident.ResolutionNote);
        }
        await using (var verifyResolved = new EePulseDbContext(fixture.Options))
        {
            var stored = await verifyResolved.AvailabilityIncidents.AsNoTracking().SingleAsync(incident => incident.Id == resolvedId, ct);
            Assert.Equal((AvailabilityIncidentStatus.Resolved, 2L, (Guid?)null, AvailabilityIncident.ConfirmedRecoveryReason),
                (stored.Status, stored.RowVersion, stored.ResolvedBy, stored.ResolutionNote));
        }
    }

    private static readonly DateTimeOffset TestTime = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static DbContextOptions<EePulseDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(connectionString).Options;

    private static async Task AssertHumanPrincipalImmutabilityObjectsAsync(
        EePulseDbContext db,
        bool expected,
        CancellationToken ct)
    {
        var triggerExists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1
                FROM pg_trigger AS trigger_object
                JOIN pg_class AS table_object ON table_object.oid = trigger_object.tgrelid
                JOIN pg_namespace AS table_schema ON table_schema.oid = table_object.relnamespace
                JOIN pg_proc AS function_object ON function_object.oid = trigger_object.tgfoid
                JOIN pg_namespace AS function_schema ON function_schema.oid = function_object.pronamespace
                WHERE NOT trigger_object.tgisinternal
                  AND table_schema.nspname = 'public'
                  AND table_object.relname = 'human_principals'
                  AND trigger_object.tgname = {HumanPrincipalImmutabilityTriggerName}
                  AND trigger_object.tgtype::integer = 27
                  AND trigger_object.tgenabled = 'O'
                  AND function_schema.nspname = 'public'
                  AND function_object.proname = {HumanPrincipalImmutabilityFunctionName}
                  AND pg_get_function_identity_arguments(function_object.oid) = ''
            ) AS "Value"
            """).SingleAsync(ct);
        var functionExists = await db.Database.SqlQuery<bool>($"""
            SELECT to_regprocedure('public.fn_wp07_human_principals_reject_mutation()') IS NOT NULL AS "Value"
            """).SingleAsync(ct);

        Assert.Equal(expected, triggerExists);
        Assert.Equal(expected, functionExists);
    }

    private static async Task<HumanPrincipalMutationSeed> SeedHumanPrincipalWithAcknowledgedIncidentAsync(
        PersistenceFixture fixture,
        CancellationToken ct)
    {
        var seed = new HumanPrincipalMutationSeed(
            Guid.NewGuid(),
            "https://issuer.example.test/immutable-original",
            "synthetic-immutable-original",
            TestTime.AddMinutes(-5),
            Guid.NewGuid(),
            "immutable actor reference");

        await using var db = new EePulseDbContext(fixture.Options);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO human_principals (id, issuer, subject, created_at)
            VALUES ({seed.PrincipalId}, {seed.Issuer}, {seed.Subject}, {seed.CreatedAt})
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO availability_incidents (
                id, probe_id, rule_key, status, opened_at,
                acknowledged_at, acknowledged_by, acknowledgement_comment)
            VALUES (
                {seed.IncidentId}, {fixture.Inventory.ProbeIds[0]}, {AvailabilityIncident.AvailabilityDownRuleKey},
                {"Acknowledged"}, {TestTime.AddMinutes(-1)}, {TestTime}, {seed.PrincipalId}, {seed.AcknowledgementComment})
            """, ct);

        return seed;
    }

    private static void AssertHumanPrincipalMutationRejected(PostgresException error)
    {
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(HumanPrincipalImmutabilityMessage, error.MessageText);
        Assert.Equal(HumanPrincipalImmutabilityTriggerName, error.ConstraintName);
    }

    private static async Task<int> AssertHumanPrincipalAndAcknowledgedActorUnchangedAsync(
        PersistenceFixture fixture,
        HumanPrincipalMutationSeed seed,
        CancellationToken ct)
    {
        await using var verify = new EePulseDbContext(fixture.Options);
        var principalCount = await verify.HumanPrincipals.AsNoTracking().CountAsync(ct);
        var principal = await verify.HumanPrincipals.AsNoTracking().SingleAsync(item => item.Id == seed.PrincipalId, ct);
        Assert.Equal((seed.PrincipalId, seed.Issuer, seed.Subject, seed.CreatedAt),
            (principal.Id, principal.Issuer, principal.Subject, principal.CreatedAt));

        var incident = await verify.AvailabilityIncidents.AsNoTracking().SingleAsync(item => item.Id == seed.IncidentId, ct);
        Assert.Equal((AvailabilityIncidentStatus.Acknowledged, seed.PrincipalId, seed.AcknowledgementComment),
            (incident.Status, incident.AcknowledgedBy, incident.AcknowledgementComment));
        Assert.Equal(1, await verify.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM availability_incidents AS incident
            JOIN human_principals AS principal ON principal.id = incident.acknowledged_by
            WHERE incident.id = {seed.IncidentId}
              AND principal.id = {seed.PrincipalId}
              AND principal.issuer = {seed.Issuer}
              AND principal.subject = {seed.Subject}
            """).SingleAsync(ct));

        return principalCount;
    }

    private static async Task AssertCurrentDowngradeSchemaAsync(EePulseDbContext verify, CancellationToken ct)
    {
        Assert.Contains(IncidentFoundationMigration, await verify.Database.GetAppliedMigrationsAsync(ct));
        Assert.Equal(3, await verify.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = 'availability_incidents'
              AND ((column_name = 'acknowledged_by' AND udt_name = 'uuid')
                OR (column_name = 'resolved_by' AND udt_name = 'uuid')
                OR (column_name = 'row_version' AND udt_name = 'int8'))
            """).SingleAsync(ct));
        Assert.Equal(4, await verify.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM pg_indexes
            WHERE schemaname = current_schema()
              AND indexname IN (
                  'ux_human_principals_issuer_subject',
                  'IX_availability_incidents_acknowledged_by',
                  'IX_availability_incidents_resolved_by',
                  'ux_availability_incidents_active_probe_rule')
            """).SingleAsync(ct));
        Assert.Equal(6, await verify.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM pg_constraint
            WHERE conrelid IN ('availability_incidents'::regclass, 'human_principals'::regclass)
              AND conname IN (
                  'ck_availability_incidents_lifecycle',
                  'ck_availability_incidents_resolution',
                  'ck_availability_incidents_row_version',
                  'ck_availability_incidents_status_lifecycle',
                  'fk_availability_incidents_human_principals_acknowledged_by',
                  'fk_availability_incidents_human_principals_resolved_by')
            """).SingleAsync(ct));
        Assert.Equal(2, await verify.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM pg_constraint
            WHERE conrelid = 'human_principals'::regclass
              AND conname IN ('ck_human_principals_issuer_shape', 'ck_human_principals_subject_shape')
            """).SingleAsync(ct));
    }

    private static async Task AssertHumanPrincipalPreservedAsync(
        EePulseDbContext verify,
        Guid principalId,
        string issuer,
        string subject,
        CancellationToken ct)
    {
        Assert.Equal(issuer, await verify.Database.SqlQuery<string>($"""
            SELECT issuer AS "Value" FROM human_principals WHERE id = {principalId}
            """).SingleAsync(ct));
        Assert.Equal(subject, await verify.Database.SqlQuery<string>($"""
            SELECT subject AS "Value" FROM human_principals WHERE id = {principalId}
            """).SingleAsync(ct));
        Assert.Equal(principalId, await verify.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM human_principals WHERE id = {principalId}
            """).SingleAsync(ct));
    }

    private static async Task AssertIncidentPreservedAsync(
        EePulseDbContext verify,
        Guid incidentId,
        Guid probeId,
        Guid actorId,
        string acknowledgementComment,
        string resolutionNote,
        CancellationToken ct)
    {
        Assert.Equal(probeId, await verify.Database.SqlQuery<Guid>($"""
            SELECT probe_id AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal("Resolved", await verify.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(TestTime, await verify.Database.SqlQuery<DateTimeOffset>($"""
            SELECT opened_at AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(TestTime, await verify.Database.SqlQuery<DateTimeOffset>($"""
            SELECT acknowledged_at AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(actorId, await verify.Database.SqlQuery<Guid?>($"""
            SELECT acknowledged_by AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(acknowledgementComment, await verify.Database.SqlQuery<string>($"""
            SELECT acknowledgement_comment AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(TestTime.AddMinutes(1), await verify.Database.SqlQuery<DateTimeOffset>($"""
            SELECT resolved_at AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(actorId, await verify.Database.SqlQuery<Guid?>($"""
            SELECT resolved_by AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(resolutionNote, await verify.Database.SqlQuery<string>($"""
            SELECT resolution_note AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(1, await verify.Database.SqlQuery<int>($"""
            SELECT occurrence_count AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(1L, await verify.Database.SqlQuery<long>($"""
            SELECT row_version AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
    }

    private static async Task AssertAutomaticIncidentPreservedAsync(
        EePulseDbContext verify,
        Guid incidentId,
        Guid probeId,
        int occurrenceCount,
        long rowVersion,
        CancellationToken ct)
    {
        Assert.Equal(incidentId, await verify.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(probeId, await verify.Database.SqlQuery<Guid>($"""
            SELECT probe_id AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(AvailabilityIncident.AvailabilityDownRuleKey, await verify.Database.SqlQuery<string>($"""
            SELECT rule_key AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal("Resolved", await verify.Database.SqlQuery<string>($"""
            SELECT status AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(TestTime, await verify.Database.SqlQuery<DateTimeOffset>($"""
            SELECT opened_at AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Null(await verify.Database.SqlQuery<DateTimeOffset?>($"""
            SELECT acknowledged_at AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Null(await verify.Database.SqlQuery<Guid?>($"""
            SELECT acknowledged_by AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Null(await verify.Database.SqlQuery<string?>($"""
            SELECT acknowledgement_comment AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(TestTime.AddMinutes(1), await verify.Database.SqlQuery<DateTimeOffset>($"""
            SELECT resolved_at AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Null(await verify.Database.SqlQuery<Guid?>($"""
            SELECT resolved_by AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(AvailabilityIncident.ConfirmedRecoveryReason, await verify.Database.SqlQuery<string>($"""
            SELECT resolution_note AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(occurrenceCount, await verify.Database.SqlQuery<int>($"""
            SELECT occurrence_count AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
        Assert.Equal(rowVersion, await verify.Database.SqlQuery<long>($"""
            SELECT row_version AS "Value" FROM availability_incidents WHERE id = {incidentId}
            """).SingleAsync(ct));
    }

    private static async Task CleanupDowngradeConcurrencyAsync(
        NpgsqlTransaction writerTransaction,
        bool writerCommitted,
        Task? migrationTask,
        bool migrationObserved,
        CancellationTokenSource behaviorTimeout,
        List<Exception> cleanupFailures)
    {
        if (migrationTask is not null && !migrationObserved)
        {
            try
            {
                behaviorTimeout.Cancel();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (!writerCommitted)
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await writerTransaction.RollbackAsync(cleanupTimeout.Token);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (migrationTask is not null && !migrationObserved)
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await migrationTask.WaitAsync(cleanupTimeout.Token);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }
    }

    private static void ThrowPrimaryOrCleanupFailure(
        ExceptionDispatchInfo? primaryFailure,
        List<Exception> cleanupFailures,
        string aggregateMessage,
        string cleanupFailureKeyPrefix)
    {
        if (primaryFailure is not null)
        {
            for (var index = 0; index < cleanupFailures.Count; index++)
            {
                primaryFailure.SourceException.Data[$"{cleanupFailureKeyPrefix}{index + 1}"] = cleanupFailures.ElementAt(index);
            }

            primaryFailure.Throw();
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures.Single()).Throw();
        }

        if (cleanupFailures.Count > 1)
        {
            throw new AggregateException(aggregateMessage, cleanupFailures);
        }
    }

    private static async Task<PersistenceFixture> CreatePersistenceFixtureAsync(int probeCount, CancellationToken ct)
    {
        var postgres = await PostgresTestDatabase.StartAsync(ct);
        try
        {
            var options = CreateOptions(postgres.ConnectionString);
            InventorySeed inventory;
            await using (var migration = new EePulseDbContext(options))
            {
                await migration.Database.MigrateAsync(ct);
                inventory = await SeedInventoryAsync(migration, probeCount, TestTime);
            }
            return new PersistenceFixture(postgres, options, inventory);
        }
        catch
        {
            await postgres.DisposeAsync();
            throw;
        }
    }

    private static async Task<InventorySeed> SeedInventoryAsync(EePulseDbContext db, int probeCount, DateTimeOffset now)
    {
        var site = new Site(Guid.NewGuid(), "WP07", "Example Corp", "UTC", now);
        var group = new AgentGroup(Guid.NewGuid(), "WP07 integration group", null, now);
        var device = new Device(Guid.NewGuid(), site.Id, "WP07 integration device", "192.0.2.70", null,
            "PLC", null, null, Criticality.Normal, [], now);
        var probes = Enumerable.Range(0, probeCount)
            .Select(_ => new Probe(Guid.NewGuid(), device.Id, group.Id, 30, 2_000, 3, 500, null, 3, 2))
            .ToArray();
        db.AddRange(site, group, device);
        db.Probes.AddRange(probes);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new InventorySeed(site.Id, group.Id, device.Id, probes.Select(probe => probe.Id).ToArray());
    }

    private static async Task<CompatibilitySeed> SeedBaselineLineageAsync(
        EePulseDbContext db, InventorySeed inventory, DateTimeOffset now, CancellationToken ct)
    {
        var agent = new EePulse.Domain.Agents.Agent(Guid.NewGuid(), inventory.AgentGroupId, Guid.NewGuid(), "wp07-migration-agent", "1.0.0", 20, now);
        var configuration = new AgentConfigurationSnapshot(inventory.AgentGroupId, 1, "{}", new byte[32], now, null);
        var acknowledgementId = Guid.NewGuid();
        var acknowledgement = new AgentConfigurationAcknowledgement(acknowledgementId, agent.Id, 1,
            AgentAcknowledgementStatus.Applied, now, now, now, null, 1, 1);
        var resultId = Guid.NewGuid();
        var eventAt = now.AddSeconds(-1);
        var ledger = new ProbeResultLedgerEntry(agent.Id, resultId, inventory.ProbeIds[0], 1,
            eventAt.AddSeconds(-1), eventAt, 1, 1, 0m, 1m, 1m, 1m, null, new byte[32], now);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 3, 2, 500, null, now);
        var binding = new ProbeStatusPolicyBinding(inventory.ProbeIds[0], 1, inventory.AgentGroupId, policy.Id);
        var boundary = new AgentConfigurationEffectiveBoundary(agent.Id, 1, acknowledgementId,
            AgentAcknowledgementStatus.Applied, now);
        var disposition = new ProbeResultProcessingDisposition(agent.Id, resultId, inventory.ProbeIds[0], eventAt,
            ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, now);
        var transition = new ProbeResultStatusTransition(agent.Id, resultId, inventory.ProbeIds[0], ProbeStatus.Recovering,
            ProbeStatus.Up, "recovery-threshold-met", eventAt, now, ProbeResultProcessingDispositionKind.StateDriving);
        const string issuer = "https://issuer.example.test/migration";
        const string subject = "synthetic-migration-subject";
        db.AddRange(agent, configuration, acknowledgement, ledger, policy, binding, boundary, disposition, transition,
            new UserTimezonePreference(issuer, subject, "Asia/Bangkok", now));
        await db.SaveChangesAsync(ct);
        return new CompatibilitySeed(agent.Id, resultId, policy.Id, issuer, subject);
    }

    private sealed record PersistenceFixture(PostgresTestDatabase Database,
        DbContextOptions<EePulseDbContext> Options, InventorySeed Inventory) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed record InventorySeed(Guid SiteId, Guid AgentGroupId, Guid DeviceId, IReadOnlyList<Guid> ProbeIds);

    private sealed record CompatibilitySeed(Guid AgentId, Guid ResultId, Guid PolicyId, string Issuer, string Subject);

    private sealed record HumanPrincipalMutationSeed(
        Guid PrincipalId,
        string Issuer,
        string Subject,
        DateTimeOffset CreatedAt,
        Guid IncidentId,
        string AcknowledgementComment);
}
