using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Execution;
using EePulse.Agent.Core.Outbox;
using EePulse.Agent.Core.Probing;
using EePulse.Agent.Core.Runtime;
using EePulse.Agent.Core.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace EePulse.Agent;

public static class AgentProbeRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAgentProbeRuntime(this IServiceCollection services)
    {
        services.AddSingleton<ILocalProbeResultSink, DurableLocalProbeResultSink>();
        services.AddSingleton<IMonotonicClock>(provider => new SystemMonotonicClock(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProbeExecutionClock>(provider => new SystemProbeExecutionClock(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProbeTransport, UnavailableProbeTransport>();
        services.AddSingleton<LocalProbeRunner>();
        services.AddSingleton<ProbeAdmissionController>();
        services.AddSingleton<IAgentScheduleSink>(provider => new AgentScheduleRuntimeCoordinator(
            provider.GetRequiredService<IMonotonicClock>(),
            provider.GetRequiredService<LocalProbeRunner>(),
            provider.GetRequiredService<ProbeAdmissionController>(),
            provider.GetRequiredService<ILocalProbeResultSink>()));
        return services;
    }

    private sealed class UnavailableProbeTransport : IProbeTransport
    {
        public ValueTask<ProbeTransportReply> SendAsync(ProbeTransportRequest request, CancellationToken cancellationToken) =>
            new(new ProbeTransportReply(ProbeTransportStatus.NetworkUnavailable, null));
    }
}
