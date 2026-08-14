using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Transport;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Core.Runtime;

public interface IAgentSelfStatus
{
    long QueueDepth { get; }

    string HealthState { get; }
}

public interface IAgentRuntimeDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class AgentRuntimeDelay(TimeProvider timeProvider) : IAgentRuntimeDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, timeProvider, cancellationToken));
}

public sealed class AgentRuntime(
    AgentApiClient apiClient,
    IAgentIdentityStore identityStore,
    IAgentConfigurationStore configurationStore,
    IPendingAcknowledgementStore pendingAcknowledgementStore,
    AgentConfigurationApplier configurationApplier,
    IAgentSelfStatus selfStatus,
    TimeProvider timeProvider,
    bool allowDevelopmentNetworks,
    IAgentRuntimeDelay? configuredRuntimeDelay = null)
{
    private bool scheduleRestored;
    private readonly IAgentRuntimeDelay runtimeDelay = configuredRuntimeDelay ?? new AgentRuntimeDelay(timeProvider);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var interval = await ExecuteCycleAsync(cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;
                await runtimeDelay.DelayAsync(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                await runtimeDelay.DelayAsync(GetBackoff(consecutiveFailures++), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await runtimeDelay.DelayAsync(GetBackoff(consecutiveFailures++), cancellationToken).ConfigureAwait(false);
            }
            catch (AgentApiException exception) when (IsTransient(exception))
            {
                await runtimeDelay.DelayAsync(GetBackoff(consecutiveFailures++), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(AgentApiException exception) =>
        exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)exception.StatusCode >= 500;

    private static TimeSpan GetBackoff(int consecutiveFailures)
    {
        var exponentialSeconds = 1 << Math.Min(consecutiveFailures, 4);
        var jitterMilliseconds = Random.Shared.Next(0, 251);
        return TimeSpan.FromSeconds(Math.Min(30, exponentialSeconds)) + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    public async ValueTask<TimeSpan> ExecuteCycleAsync(CancellationToken cancellationToken)
    {
        var identity = await identityStore.LoadAsync(cancellationToken).ConfigureAwait(false) ??
                       throw new InvalidOperationException("Agent enrollment is required.");
        var storedConfiguration = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!scheduleRestored && storedConfiguration is not null)
        {
            await configurationApplier.RestoreAsync(
                identity,
                storedConfiguration,
                allowDevelopmentNetworks,
                cancellationToken).ConfigureAwait(false);
            scheduleRestored = true;
        }

        var pendingAcknowledgement = await pendingAcknowledgementStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (pendingAcknowledgement is not null)
        {
            await SendPersistedAcknowledgementAsync(identity, pendingAcknowledgement, cancellationToken).ConfigureAwait(false);
            identity = await identityStore.LoadAsync(cancellationToken).ConfigureAwait(false) ??
                       throw new InvalidOperationException("Agent identity was removed.");
        }

        var heartbeatId = Guid.NewGuid();
        var heartbeat = new AgentHeartbeatRequest(
            AgentContract.SchemaVersion,
            heartbeatId,
            identity.AgentVersion,
            identity.MachineName,
            storedConfiguration?.Active.ConfigurationVersion ?? 0,
            Math.Max(0, selfStatus.QueueDepth),
            selfStatus.HealthState,
            timeProvider.GetUtcNow());
        var heartbeatResponse = await apiClient.SendHeartbeatAsync(identity, heartbeat, cancellationToken)
            .ConfigureAwait(false);

        identity = await identityStore.LoadAsync(cancellationToken).ConfigureAwait(false) ??
                   throw new InvalidOperationException("Agent identity was removed.");
        if (identity.DesiredConfigurationVersion != heartbeatResponse.DesiredConfigurationVersion ||
            identity.HeartbeatIntervalSeconds != heartbeatResponse.NextHeartbeatSeconds)
        {
            identity = identity with
            {
                DesiredConfigurationVersion = heartbeatResponse.DesiredConfigurationVersion,
                HeartbeatIntervalSeconds = heartbeatResponse.NextHeartbeatSeconds,
            };
            await identityStore.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
        }

        if (heartbeatResponse.CredentialRotationRequired)
        {
            identity = await apiClient.RotateCredentialAsync(identity, cancellationToken).ConfigureAwait(false);
        }

        if (heartbeatResponse.ConfigurationChanged ||
            storedConfiguration?.Active.ConfigurationVersion != heartbeatResponse.DesiredConfigurationVersion)
        {
            (AgentConfigurationResponse Configuration, string StrongETag)? pulled;
            try
            {
                pulled = await apiClient.PullConfigurationAsync(
                    identity,
                    storedConfiguration?.StrongETag,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AgentConfigurationPayloadException exception)
            {
                await SendAcknowledgementAsync(
                    identity,
                    exception.ConfigurationVersion,
                    ConfigurationApplyResult.Rejected(
                        storedConfiguration?.Active.ConfigurationVersion ?? 0,
                        ConfigurationRejectionCode.ProbeInvalid),
                    cancellationToken).ConfigureAwait(false);
                return TimeSpan.FromSeconds(Math.Clamp(heartbeatResponse.NextHeartbeatSeconds, 15, 30));
            }

            if (pulled is not null)
            {
                var result = await configurationApplier.ApplyAsync(
                    identity,
                    pulled.Value.Configuration,
                    pulled.Value.StrongETag,
                    allowDevelopmentNetworks,
                    cancellationToken).ConfigureAwait(false);
                scheduleRestored = result.Applied;
                await SendAcknowledgementAsync(identity, pulled.Value.Configuration.ConfigurationVersion, result, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return TimeSpan.FromSeconds(Math.Clamp(heartbeatResponse.NextHeartbeatSeconds, 15, 30));
    }

    private async ValueTask SendAcknowledgementAsync(
        AgentIdentity identity,
        long configurationVersion,
        ConfigurationApplyResult result,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var acknowledgement = new AgentConfigurationAcknowledgementRequest(
            AgentContract.SchemaVersion,
            Guid.NewGuid(),
            configurationVersion,
            result.Applied ? "Applied" : "Rejected",
            result.Applied ? now : null,
            result.RejectionCode is null ? null : ToWireErrorCode(result.RejectionCode.Value),
            now);
        await pendingAcknowledgementStore.SaveAsync(acknowledgement, cancellationToken).ConfigureAwait(false);
        await SendPersistedAcknowledgementAsync(identity, acknowledgement, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendPersistedAcknowledgementAsync(
        AgentIdentity identity,
        AgentConfigurationAcknowledgementRequest acknowledgement,
        CancellationToken cancellationToken)
    {
        try
        {
            await apiClient.AcknowledgeConfigurationAsync(identity, acknowledgement, cancellationToken).ConfigureAwait(false);
            await pendingAcknowledgementStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AgentApiException exception) when (
            string.Equals(exception.Code, AgentProblemCodes.AcknowledgementConflict, StringComparison.Ordinal))
        {
            await pendingAcknowledgementStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static string ToWireErrorCode(ConfigurationRejectionCode code) => code switch
    {
        ConfigurationRejectionCode.SchemaUnsupported => AgentConfigurationRejectionCodes.SchemaUnsupported,
        ConfigurationRejectionCode.IdentityMismatch => AgentConfigurationRejectionCodes.ConfigurationInvalid,
        ConfigurationRejectionCode.VersionNotMonotonic => AgentConfigurationRejectionCodes.ConfigurationInvalid,
        ConfigurationRejectionCode.NetworkPolicyMismatch => AgentConfigurationRejectionCodes.NetworkPolicyMismatch,
        ConfigurationRejectionCode.ProbeInvalid => AgentConfigurationRejectionCodes.ConfigurationInvalid,
        ConfigurationRejectionCode.DuplicateProbe => AgentConfigurationRejectionCodes.ConfigurationInvalid,
        ConfigurationRejectionCode.ConfigurationTooLarge => AgentConfigurationRejectionCodes.ConfigurationInvalid,
        ConfigurationRejectionCode.ConfigurationStorageFailed => AgentConfigurationRejectionCodes.ConfigurationStorageFailed,
        ConfigurationRejectionCode.SchedulerApplyFailed => AgentConfigurationRejectionCodes.SchedulerApplyFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}
