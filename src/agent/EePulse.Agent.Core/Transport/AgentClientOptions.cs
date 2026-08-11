namespace EePulse.Agent.Core.Transport;

public sealed record AgentClientOptions(Uri ServerBaseAddress, bool IsProduction)
{
    public void Validate()
    {
        if (!ServerBaseAddress.IsAbsoluteUri ||
            (IsProduction && !string.Equals(ServerBaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            (!string.Equals(ServerBaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(ServerBaseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Agent server configuration is invalid or insecure.");
        }
    }
}

public interface IAgentRetryDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class AgentRetryDelay(TimeProvider timeProvider) : IAgentRetryDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, timeProvider, cancellationToken));
}
