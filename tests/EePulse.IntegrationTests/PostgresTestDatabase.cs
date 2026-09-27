using EePulse.Infrastructure.Persistence;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EePulse.IntegrationTests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer? _container;
    private readonly bool _ownsDatabase;

    private PostgresTestDatabase(string connectionString, PostgreSqlContainer? container, bool ownsDatabase)
    {
        ConnectionString = connectionString;
        _container = container;
        _ownsDatabase = ownsDatabase;
    }

    public string ConnectionString { get; private set; }

    // Browser acceptance must never reuse external/shared databases or fixed credentials.
    public static async Task<PostgresTestDatabase> StartIsolatedAsync(Action<PostgresTestDatabase> registerOwnership,
        CancellationToken cancellationToken)
    {
        var container = new PostgreSqlBuilder("postgres:18.4-alpine")
            .WithUsername("ui_acceptance")
            .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
            .Build();
        // Transfer ownership before startup: the caller's failure-preserving cleanup
        // also owns a partially started container. Never dispose it a second time here.
        var database = new PostgresTestDatabase(string.Empty, container, false);
        registerOwnership(database);
        await container.StartAsync(cancellationToken);
        database.ConnectionString = container.GetConnectionString();
        return database;
    }

    public static async Task<PostgresTestDatabase> StartAsync(CancellationToken cancellationToken)
    {
        var external = Environment.GetEnvironmentVariable("EE_PULSE_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            var builder = new NpgsqlConnectionStringBuilder(external)
            {
                Database = $"ee_pulse_test_{Guid.NewGuid():N}"
            };
            return new PostgresTestDatabase(builder.ConnectionString, null, true);
        }

        var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(cancellationToken);
        return new PostgresTestDatabase(container.GetConnectionString(), container, false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDatabase)
        {
            var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(ConnectionString).Options;
            await using var db = new EePulseDbContext(options);
            await db.Database.EnsureDeletedAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
