using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Security;

namespace EePulse.Agent.Infrastructure.Storage;

public sealed class ProtectedAgentIdentityStore : IAgentIdentityStore, IDisposable
{
    private readonly AtomicProtectedJsonFile<AgentIdentity> file;

    public ProtectedAgentIdentityStore(
        AgentStorageOptions options,
        ISecretProtector protector,
        IProtectedFileAccessPolicy accessPolicy)
    {
        options.Validate();
        file = new AtomicProtectedJsonFile<AgentIdentity>(
            Path.Combine(options.RootDirectory, "identity.dat"),
            protector,
            accessPolicy);
    }

    public ValueTask<AgentIdentity?> LoadAsync(CancellationToken cancellationToken) => file.LoadAsync(cancellationToken);

    public ValueTask SaveAsync(AgentIdentity identity, CancellationToken cancellationToken) =>
        file.SaveAsync(identity, cancellationToken);

    public ValueTask DeleteAsync(CancellationToken cancellationToken) => file.DeleteAsync(cancellationToken);

    public void Dispose() => file.Dispose();
}
