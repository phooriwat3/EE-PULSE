using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EePulse.Infrastructure.Persistence;

public sealed class EePulseDbContextFactory : IDesignTimeDbContextFactory<EePulseDbContext>
{
    public EePulseDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("EE_PULSE_POSTGRES_CONNECTION") ??
            "Host=localhost;Port=5432;Database=ee_pulse;Username=ee_pulse;Password=local-development-only";
        var options = new DbContextOptionsBuilder<EePulseDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new EePulseDbContext(options);
    }
}
