using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Security;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Infrastructure.Storage;

public sealed class ProtectedPendingAcknowledgementStore : IPendingAcknowledgementStore, IDisposable
{
    private readonly AtomicProtectedJsonFile<AgentConfigurationAcknowledgementRequest> file;

    public ProtectedPendingAcknowledgementStore(
        AgentStorageOptions options,
        ISecretProtector protector,
        IProtectedFileAccessPolicy accessPolicy)
    {
        options.Validate();
        file = new AtomicProtectedJsonFile<AgentConfigurationAcknowledgementRequest>(
            Path.Combine(options.RootDirectory, "pending-acknowledgement.dat"),
            protector,
            accessPolicy);
    }

    public ValueTask<AgentConfigurationAcknowledgementRequest?> LoadAsync(CancellationToken cancellationToken) =>
        file.LoadAsync(cancellationToken);

    public ValueTask SaveAsync(
        AgentConfigurationAcknowledgementRequest acknowledgement,
        CancellationToken cancellationToken) => file.SaveAsync(acknowledgement, cancellationToken);

    public ValueTask DeleteAsync(CancellationToken cancellationToken) => file.DeleteAsync(cancellationToken);

    public void Dispose() => file.Dispose();
}
