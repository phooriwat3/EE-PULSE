using EePulse.Domain.Preferences;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Text.RegularExpressions;

namespace EePulse.IntegrationTests;

public sealed class UserTimezonePreferencePersistenceTests
{
    private const string Issuer = "https://issuer.test/wp07";
    private const string Subject = "subject-wp07-001";
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MigrationSchemaConcurrencyClearNoOpAndRollbackAreVerified()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;

        await using (var migration = new EePulseDbContext(options))
        {
            await migration.Database.MigrateAsync(ct);
            Assert.False(migration.Database.HasPendingModelChanges());
            var migrations = (await migration.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            var current = migrations[^1];
            var previous = migrations[^2];
            var forwardSql = migration.Database.GetService<IMigrator>().GenerateScript(previous, current);
            var rollbackSql = migration.Database.GetService<IMigrator>().GenerateScript(current, previous);
            Assert.Equal([new SchemaChange("CREATE TABLE", "user_timezone_preferences")], DiscoverSchemaChanges(forwardSql));
            Assert.Equal([new SchemaChange("DROP TABLE", "user_timezone_preferences")], DiscoverSchemaChanges(rollbackSql));
        }

        await using (var schema = new NpgsqlConnection(postgres.ConnectionString))
        {
            await schema.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT column_name, is_nullable, data_type, character_maximum_length FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'user_timezone_preferences' ORDER BY ordinal_position", schema);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var columns = new List<(string Name, string Nullable, string Type, int? Length)>();
            while (await reader.ReadAsync(ct))
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3)));
            Assert.Equal([
                ("issuer", "NO", "character varying", 512),
                ("subject", "NO", "character varying", 512),
                ("timezone", "YES", "character varying", 255),
                ("version", "NO", "bigint", (int?)null),
                ("created_at", "NO", "timestamp with time zone", (int?)null),
                ("updated_at", "NO", "timestamp with time zone", (int?)null)], columns);
        }

        await using (var create = new EePulseDbContext(options))
        {
            create.Add(new UserTimezonePreference(Issuer, Subject, "Asia/Bangkok", Now));
            await create.SaveChangesAsync(ct);
        }

        await using (var noOp = new EePulseDbContext(options))
        {
            var preference = await noOp.UserTimezonePreferences.SingleAsync(ct);
            Assert.False(preference.ApplyTimezone("Asia/Bangkok", Now.AddHours(1)));
            await noOp.SaveChangesAsync(ct);
        }
        await using (var verifyNoOp = new EePulseDbContext(options))
        {
            var preference = await verifyNoOp.UserTimezonePreferences.SingleAsync(ct);
            Assert.Equal(1, preference.Version);
            Assert.Equal(Now, preference.UpdatedAt);
        }

        await using (var clear = new EePulseDbContext(options))
        {
            var preference = await clear.UserTimezonePreferences.SingleAsync(ct);
            Assert.True(preference.ApplyTimezone(null, Now.AddMinutes(1)));
            await clear.SaveChangesAsync(ct);
        }
        await using (var verifyClear = new EePulseDbContext(options))
        {
            var preference = await verifyClear.UserTimezonePreferences.SingleAsync(ct);
            Assert.Equal(Subject, preference.Subject);
            Assert.Null(preference.Timezone);
            Assert.Equal(2, preference.Version);
            Assert.Equal(Now.AddMinutes(1), preference.UpdatedAt);
        }

        await using (var reset = new EePulseDbContext(options))
        {
            var preference = await reset.UserTimezonePreferences.SingleAsync(ct);
            Assert.True(preference.ApplyTimezone("Etc/UTC", Now.AddMinutes(2)));
            await reset.SaveChangesAsync(ct);
        }

        var persistedBeforeCreatedAtAttempt = await ReadPreferenceAsync(options, ct);
        await using (var createdAtMutation = new EePulseDbContext(options))
        {
            var preference = await createdAtMutation.UserTimezonePreferences.SingleAsync(ct);
            var entry = createdAtMutation.Entry(preference);
            entry.Property(x => x.CreatedAt).CurrentValue = preference.CreatedAt.AddMinutes(10);
            await Assert.ThrowsAsync<InvalidOperationException>(() => createdAtMutation.SaveChangesAsync(ct));
        }
        var persistedAfterCreatedAtAttempt = await ReadPreferenceAsync(options, ct);
        Assert.Equal(persistedBeforeCreatedAtAttempt, persistedAfterCreatedAtAttempt);

        await using var first = new EePulseDbContext(options);
        await using var second = new EePulseDbContext(options);
        var firstPreference = await first.UserTimezonePreferences.SingleAsync(ct);
        var secondPreference = await second.UserTimezonePreferences.SingleAsync(ct);
        Assert.True(firstPreference.ApplyTimezone("Asia/Kolkata", Now.AddMinutes(3)));
        Assert.True(secondPreference.ApplyTimezone("Asia/Tokyo", Now.AddMinutes(4)));
        await first.SaveChangesAsync(ct);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(ct));

        await using (var winner = new EePulseDbContext(options))
        {
            var preference = await winner.UserTimezonePreferences.SingleAsync(ct);
            Assert.Equal("Asia/Kolkata", preference.Timezone);
            Assert.Equal(4, preference.Version);
        }

        await using var duplicateA = new EePulseDbContext(options);
        await using var duplicateB = new EePulseDbContext(options);
        duplicateA.Add(new UserTimezonePreference("https://issuer.test/duplicate", "subject", "Asia/Bangkok", Now));
        duplicateB.Add(new UserTimezonePreference("https://issuer.test/duplicate", "subject", "Asia/Bangkok", Now));
        var insertResults = await Task.WhenAll(
            CaptureSaveAsync(duplicateA, ct),
            CaptureSaveAsync(duplicateB, ct));
        Assert.Single(insertResults, exception => exception is null);
        Assert.Single(insertResults, exception => exception is DbUpdateException);
    }

    private static async Task<Exception?> CaptureSaveAsync(EePulseDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return null; }
        catch (Exception exception) { return exception; }
    }

    private static async Task<PreferenceSnapshot> ReadPreferenceAsync(DbContextOptions<EePulseDbContext> options, CancellationToken ct)
    {
        await using var db = new EePulseDbContext(options);
        var preference = await db.UserTimezonePreferences.AsNoTracking().SingleAsync(ct);
        return new PreferenceSnapshot(preference.Issuer, preference.Subject, preference.Timezone, preference.Version, preference.CreatedAt, preference.UpdatedAt);
    }

    private static List<SchemaChange> DiscoverSchemaChanges(string sql)
    {
        var changes = new List<SchemaChange>();
        foreach (var raw in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var statement = Regex.Replace(raw, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
            if (statement is "START TRANSACTION" or "COMMIT" ||
                statement.StartsWith("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal) ||
                statement.StartsWith("DELETE FROM \"__EFMigrationsHistory\"", StringComparison.Ordinal))
                continue;

            var match = Regex.Match(statement, "^(?<kind>CREATE TABLE|DROP TABLE) (?:(?:IF NOT EXISTS|IF EXISTS) )?(?<quoted>\"[^\"]+\"|[A-Za-z_][A-Za-z0-9_]*)(?:\\s|\\(|$)", RegexOptions.CultureInvariant);
            changes.Add(match.Success
                ? new SchemaChange(match.Groups["kind"].Value, NormalizeIdentifier(match.Groups["quoted"].Value))
                : new SchemaChange("UNRECOGNIZED DDL", statement));
        }
        return changes;
    }

    private static string NormalizeIdentifier(string identifier) =>
        identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"' ? identifier[1..^1] : identifier;

    private sealed record PreferenceSnapshot(string Issuer, string Subject, string? Timezone, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record SchemaChange(string Kind, string Target);
}
