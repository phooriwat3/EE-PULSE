using EePulse.Agent.Core.Configuration;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Core.Runtime;

public sealed class InactiveAgentScheduleSink : IAgentScheduleSink
{
    private AgentConfigurationResponse? active;

    public AgentConfigurationResponse? Active => Volatile.Read(ref active);

    public ValueTask ReplaceAsync(AgentConfigurationResponse configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref active, configuration);
        return ValueTask.CompletedTask;
    }

    public ValueTask HaltAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref active, null);
        return ValueTask.CompletedTask;
    }
}

public sealed class DefaultAgentSelfStatus : IAgentSelfStatus
{
    public long QueueDepth => 0;

    public string HealthState => "Healthy";
}
