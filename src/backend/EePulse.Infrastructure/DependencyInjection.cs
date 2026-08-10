using EePulse.Application.Time;
using EePulse.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace EePulse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEePulseInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IUtcClock, SystemUtcClock>();
        return services;
    }
}
