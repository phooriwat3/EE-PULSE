using System.Net;

namespace EePulse.Agent.Core.Transport;

public sealed class AgentApiException(HttpStatusCode statusCode, string? code = null)
    : Exception("The Agent API rejected the operation.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Code { get; } = code;
}

public sealed class AgentConfigurationPayloadException(long configurationVersion)
    : Exception("The configuration payload violates the closed Agent schema.")
{
    public long ConfigurationVersion { get; } = configurationVersion;
}
