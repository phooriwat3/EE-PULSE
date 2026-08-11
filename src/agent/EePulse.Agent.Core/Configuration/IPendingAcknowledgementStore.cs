using EePulse.Contracts.Agents;

namespace EePulse.Agent.Core.Configuration;

public interface IPendingAcknowledgementStore
{
    ValueTask<AgentConfigurationAcknowledgementRequest?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(
        AgentConfigurationAcknowledgementRequest acknowledgement,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(CancellationToken cancellationToken);
}
