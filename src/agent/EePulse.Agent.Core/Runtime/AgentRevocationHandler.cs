using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Identity;

namespace EePulse.Agent.Core.Runtime;

public interface IAgentRevocationHandler
{
    ValueTask HandleRevocationAsync(CancellationToken cancellationToken);
}

public sealed class AgentRevocationHandler(
    IAgentScheduleSink scheduleSink,
    IAgentIdentityStore identityStore,
    IPendingAcknowledgementStore? pendingAcknowledgementStore = null) : IAgentRevocationHandler
{
    private int revoked;

    public bool IsRevoked => Volatile.Read(ref revoked) != 0;

    public async ValueTask HandleRevocationAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref revoked, 1) != 0)
        {
            return;
        }

        await scheduleSink.HaltAsync(cancellationToken).ConfigureAwait(false);
        if (pendingAcknowledgementStore is not null)
        {
            await pendingAcknowledgementStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await identityStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
