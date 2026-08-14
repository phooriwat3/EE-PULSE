using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Security;

namespace EePulse.Agent.Infrastructure.Storage;

public sealed class ProtectedAgentConfigurationStore : IAgentConfigurationStore, IDisposable
{
    private readonly AtomicProtectedJsonFile<StoredAgentConfiguration> file;

    public ProtectedAgentConfigurationStore(
        AgentStorageOptions options,
        ISecretProtector protector,
        IProtectedFileAccessPolicy accessPolicy)
    {
        options.Validate();
        file = new AtomicProtectedJsonFile<StoredAgentConfiguration>(
            Path.Combine(options.RootDirectory, "configuration.dat"),
            protector,
            accessPolicy);
    }

    public ValueTask<StoredAgentConfiguration?> LoadAsync(CancellationToken cancellationToken) =>
        file.LoadAsync(cancellationToken);

    public ValueTask SaveAsync(StoredAgentConfiguration configuration, CancellationToken cancellationToken) =>
        file.SaveAsync(configuration, cancellationToken);

    public ValueTask DeleteAsync(CancellationToken cancellationToken) => file.DeleteAsync(cancellationToken);

    public void Dispose() => file.Dispose();
}
