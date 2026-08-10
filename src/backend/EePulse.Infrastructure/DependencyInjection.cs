using EePulse.Application.Time;
using EePulse.Infrastructure.Time;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EePulse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEePulseInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IUtcClock, SystemUtcClock>();
        return services;
    }

    public static IServiceCollection AddEePulsePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Postgres is required for PostgreSQL persistence.");
        }

        services.AddDbContext<EePulseDbContext>(options => options.UseNpgsql(connectionString));
        return services;
    }
}
