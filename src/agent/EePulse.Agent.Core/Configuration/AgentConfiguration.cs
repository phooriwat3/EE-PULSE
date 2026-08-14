using EePulse.Contracts.Agents;

namespace EePulse.Agent.Core.Configuration;

public sealed record StoredAgentConfiguration(
    AgentConfigurationResponse Active,
    AgentConfigurationResponse? LastKnownGood,
    string StrongETag);

public interface IAgentConfigurationStore
{
    ValueTask<StoredAgentConfiguration?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(StoredAgentConfiguration configuration, CancellationToken cancellationToken);

    ValueTask DeleteAsync(CancellationToken cancellationToken);
}

public interface IAgentScheduleSink
{
    ValueTask ReplaceAsync(AgentConfigurationResponse configuration, CancellationToken cancellationToken);

    ValueTask HaltAsync(CancellationToken cancellationToken);
}
