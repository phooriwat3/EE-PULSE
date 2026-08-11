using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Networking;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Core.Configuration;

public sealed class AgentConfigurationApplier(
    AgentConfigurationValidator validator,
    IAgentConfigurationStore store,
    IAgentScheduleSink scheduleSink) : IDisposable
{
    private readonly SemaphoreSlim applyLock = new(1, 1);

    public async ValueTask RestoreAsync(
        AgentIdentity identity,
        StoredAgentConfiguration stored,
        bool allowDevelopmentNetworks,
        CancellationToken cancellationToken)
    {
        if (!AllowedNetworkPolicy.TryCreate(identity.LocalAllowedNetworks, allowDevelopmentNetworks, out var localCeiling) ||
            validator.Validate(
                stored.Active,
                identity.AgentId,
                identity.AgentGroupId,
                stored.Active.ConfigurationVersion,
                localCeiling!,
                allowEqualVersion: true) is not null)
        {
            await scheduleSink.HaltAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Stored Agent configuration failed security validation.");
        }

        await scheduleSink.ReplaceAsync(stored.Active, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConfigurationApplyResult> ApplyAsync(
        AgentIdentity identity,
        AgentConfigurationResponse candidate,
        string strongETag,
        bool allowDevelopmentNetworks,
        CancellationToken cancellationToken)
    {
        await applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var activeVersion = previous?.Active.ConfigurationVersion ?? 0;
            if (!AllowedNetworkPolicy.TryCreate(identity.LocalAllowedNetworks, allowDevelopmentNetworks, out var localCeiling))
            {
                return ConfigurationApplyResult.Rejected(activeVersion, ConfigurationRejectionCode.NetworkPolicyMismatch);
            }

            var rejection = validator.Validate(
                candidate,
                identity.AgentId,
                identity.AgentGroupId,
                activeVersion,
                localCeiling!);
            if (rejection is not null)
            {
                return ConfigurationApplyResult.Rejected(activeVersion, rejection.Value);
            }

            var next = new StoredAgentConfiguration(candidate, previous?.Active, strongETag);
            try
            {
                await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return ConfigurationApplyResult.Rejected(
                    activeVersion,
                    ConfigurationRejectionCode.ConfigurationStorageFailed);
            }

            try
            {
                await scheduleSink.ReplaceAsync(candidate, cancellationToken).ConfigureAwait(false);
                return ConfigurationApplyResult.Success(candidate.ConfigurationVersion);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RollbackAsync(previous).ConfigureAwait(false);
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                var rollbackSucceeded = await RollbackAsync(previous).ConfigureAwait(false);

                return ConfigurationApplyResult.Rejected(
                    activeVersion,
                    rollbackSucceeded
                        ? ConfigurationRejectionCode.SchedulerApplyFailed
                        : ConfigurationRejectionCode.ConfigurationStorageFailed);
            }
        }
        finally
        {
            applyLock.Release();
        }
    }

    private async ValueTask<bool> RollbackAsync(StoredAgentConfiguration? previous)
    {
        try
        {
            if (previous is not null)
            {
                await store.SaveAsync(previous, CancellationToken.None).ConfigureAwait(false);
                await scheduleSink.ReplaceAsync(previous.Active, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await store.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                await scheduleSink.HaltAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception)
        {
            await scheduleSink.HaltAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    public void Dispose() => applyLock.Dispose();
}
