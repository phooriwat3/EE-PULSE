using EePulse.Agent.Core.Networking;

namespace EePulse.Agent.Core.Probing;

public sealed class AllowedNetworkProbeTransport(
    IProbeTransport inner,
    Func<AllowedNetworkPolicy?> localPolicyProvider,
    Func<AllowedNetworkPolicy?> activeRemotePolicyProvider) : IProbeTransport
{
    public ValueTask<ProbeTransportReply> SendAsync(
        ProbeTransportRequest request,
        CancellationToken cancellationToken)
    {
        var localPolicy = localPolicyProvider();
        var remotePolicy = activeRemotePolicyProvider();
        if (localPolicy is null || remotePolicy is null ||
            !localPolicy.Contains(request.Target) || !remotePolicy.Contains(request.Target))
        {
            throw new NetworkPolicyViolationException();
        }

        return inner.SendAsync(request, cancellationToken);
    }
}

public sealed class NetworkPolicyViolationException : InvalidOperationException
{
    public NetworkPolicyViolationException()
        : base("The probe target is outside the locally approved network boundary.")
    {
    }
}
