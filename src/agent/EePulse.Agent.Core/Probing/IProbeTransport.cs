namespace EePulse.Agent.Core.Probing;

/// <summary>
/// Low-level transport seam. Implementations perform one attempt and must honor cancellation.
/// </summary>
public interface IProbeTransport
{
    ValueTask<ProbeTransportReply> SendAsync(
        ProbeTransportRequest request,
        CancellationToken cancellationToken);
}

public sealed record ProbeTransportRequest(string Target, TimeSpan Timeout);

public sealed record ProbeTransportReply(ProbeTransportStatus Status, TimeSpan? RoundTripTime);

public enum ProbeTransportStatus
{
    Succeeded,
    TimedOut,
    Unreachable,
    Failed,
}
