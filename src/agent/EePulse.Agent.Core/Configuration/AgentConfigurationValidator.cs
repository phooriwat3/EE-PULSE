using EePulse.Agent.Core.Networking;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Core.Configuration;

public sealed class AgentConfigurationValidator(bool allowDevelopmentNetworks)
{
    public ConfigurationRejectionCode? Validate(
        AgentConfigurationResponse candidate,
        Guid expectedAgentId,
        Guid expectedAgentGroupId,
        long activeVersion,
        AllowedNetworkPolicy localCeiling,
        bool allowEqualVersion = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(localCeiling);

        if (candidate.SchemaVersion != AgentContract.SchemaVersion)
        {
            return ConfigurationRejectionCode.SchemaUnsupported;
        }

        if (candidate.AgentId != expectedAgentId || candidate.AgentGroupId != expectedAgentGroupId)
        {
            return ConfigurationRejectionCode.IdentityMismatch;
        }

        if (candidate.ConfigurationVersion < activeVersion ||
            (!allowEqualVersion && candidate.ConfigurationVersion == activeVersion) ||
            candidate.ConfigurationVersion < 1)
        {
            return ConfigurationRejectionCode.VersionNotMonotonic;
        }

        if (candidate.Probes.Count > 2_000)
        {
            return ConfigurationRejectionCode.ConfigurationTooLarge;
        }

        if (!AllowedNetworkPolicy.TryCreate(candidate.AllowedNetworks, allowDevelopmentNetworks, out var remotePolicy) ||
            remotePolicy!.Networks.Any(network => !Ipv4Network.TryParse(network, out var parsed) || !localCeiling.Contains(parsed)))
        {
            return ConfigurationRejectionCode.NetworkPolicyMismatch;
        }

        var probeIds = new HashSet<Guid>();
        foreach (var probe in candidate.Probes)
        {
            if (!probeIds.Add(probe.ProbeId))
            {
                return ConfigurationRejectionCode.DuplicateProbe;
            }

            if (!IsValidProbe(probe) ||
                !remotePolicy.Contains(probe.TargetAddress) ||
                !localCeiling.Contains(probe.TargetAddress))
            {
                return ConfigurationRejectionCode.ProbeInvalid;
            }
        }

        return null;
    }

    private static bool IsValidProbe(AgentProbeConfiguration probe) =>
        probe.ProbeId != Guid.Empty &&
        probe.DeviceId != Guid.Empty &&
        probe.ProbeConfigVersion >= 1 &&
        string.Equals(probe.Type, "icmp", StringComparison.Ordinal) &&
        probe.IntervalSeconds is >= 5 and <= 3_600 &&
        probe.TimeoutMilliseconds is >= 100 and <= 60_000 &&
        probe.AttemptCount is >= 1 and <= 10 &&
        probe.WarningRttMilliseconds is null or >= 1 &&
        probe.CriticalRttMilliseconds is null or >= 1 &&
        (probe.WarningRttMilliseconds is null || probe.CriticalRttMilliseconds is null ||
         probe.CriticalRttMilliseconds >= probe.WarningRttMilliseconds) &&
        probe.FailureThreshold is >= 1 and <= 100 &&
        probe.RecoveryThreshold is >= 1 and <= 100;
}
