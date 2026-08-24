using EePulse.Agent.Core.Identity;
using EePulse.Agent.Core.Probing;

namespace EePulse.Agent.Core.Outbox;

/// <summary>Persists completed probe results before their worker may continue.</summary>
public sealed class DurableLocalProbeResultSink(IProbeResultOutbox outbox, IAgentIdentityStore identityStore) : ILocalProbeResultSink
{
    public void Publish(LocalProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var identity = identityStore.LoadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult() ??
                       throw new InvalidOperationException("Agent identity is required before durable result persistence.");
        outbox.EnqueueAsync(ProbeResultEnvelope.Create(identity.AgentId, result), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }
}
