using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using EePulse.Contracts.Agents;

namespace EePulse.Api.OpenApi;

public sealed class InventorySecurityDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";
    private static readonly HashSet<string> Wp03Paths = new(StringComparer.Ordinal)
    {
        "/api/v1/agent-enrollment-tokens",
        "/api/v1/agent-enrollment-tokens/{tokenId}",
        "/api/v1/agents/enroll",
        "/api/v1/agent-groups/{agentGroupId}/allowed-networks",
        "/api/v1/agents/{agentId}/allowed-networks",
        "/api/v1/agent-groups/{agentGroupId}/configuration/rollback",
        "/api/v1/agents/{agentId}/revoke",
        "/api/v1/agents/{agentId}/heartbeat",
        "/api/v1/agents/{agentId}/configuration",
        "/api/v1/agents/{agentId}/configuration/acknowledgements",
        "/api/v1/agents/{agentId}/credentials/rotate"
    };

    private static readonly HashSet<string> ClosedWp03Schemas = new(StringComparer.Ordinal)
    {
        "CreateAgentEnrollmentTokenRequest", "CreateAgentEnrollmentTokenResponse",
        "AgentEnrollmentRequest", "AgentEnrollmentResponse", "AgentResponse", "PagedAgentResponse",
        "AgentHeartbeatRequest", "AgentHeartbeatResponse", "AgentConfigurationResponse", "AgentProbeConfiguration",
        "AgentConfigurationAcknowledgementRequest", "AgentConfigurationAcknowledgementResponse",
        "RotateAgentCredentialRequest", "RotateAgentCredentialResponse", "RevokeAgentRequest",
        "UpdateAgentAllowedNetworksRequest", "UpdateAgentGroupAllowedNetworksRequest", "AgentNetworkPolicyResponse",
        "RollbackAgentConfigurationRequest", "AgentConfigurationPublicationResponse"
    };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Production-oriented OIDC bearer access token. In Development only, EE Pulse currently " +
                "uses the synthetic X-EE-Pulse-Role and X-EE-Pulse-Actor headers instead of issuing bearer tokens."
        };
        document.Components.SecuritySchemes[AgentContract.CredentialAuthenticationScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = AgentContract.CredentialBearerFormat,
            Description = "Per-Agent opaque credential issued by the enrollment flow."
        };

        foreach (var path in document.Paths.Where(candidate => candidate.Key.StartsWith("/api/v1/", StringComparison.Ordinal)))
        {
            if (path.Value.Operations is null)
            {
                continue;
            }

            foreach (var operation in path.Value.Operations.Values)
            {
                operation.Security ??= [];
                var isEnrollment = path.Key == "/api/v1/agents/enroll";
                var isAgentOperation = path.Key.StartsWith("/api/v1/agents/{agentId}/heartbeat", StringComparison.Ordinal) ||
                    path.Key.StartsWith("/api/v1/agents/{agentId}/configuration", StringComparison.Ordinal) ||
                    path.Key.StartsWith("/api/v1/agents/{agentId}/credentials/rotate", StringComparison.Ordinal);
                if (!isEnrollment)
                {
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(isAgentOperation ? AgentContract.CredentialAuthenticationScheme : SchemeName, document)] = []
                    });
                }
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentication is required or invalid." });
                if (!isEnrollment) operation.Responses.TryAdd("403", new OpenApiResponse { Description = "The authenticated principal lacks the required role or policy." });
                if (path.Key == "/api/v1/agents/{agentId}/configuration")
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter { Name = "If-None-Match", In = ParameterLocation.Header, Required = false, Schema = new OpenApiSchema { Type = JsonSchemaType.String } });
                    if (operation.Responses.TryGetValue("200", out var ok) && ok is OpenApiResponse okResponse)
                    { okResponse.Headers ??= new Dictionary<string, IOpenApiHeader>(); okResponse.Headers["ETag"] = new OpenApiHeader { Description = "Strong configuration entity tag.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } }; }
                    if (operation.Responses.TryGetValue("304", out var notModified) && notModified is OpenApiResponse notModifiedResponse)
                    { notModifiedResponse.Headers ??= new Dictionary<string, IOpenApiHeader>(); notModifiedResponse.Headers["ETag"] = new OpenApiHeader { Description = "Strong configuration entity tag.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } }; }
                }
                if (Wp03Paths.Contains(path.Key) && operation.Responses.TryGetValue("429", out var limited) && limited is OpenApiResponse limitedResponse)
                { limitedResponse.Headers ??= new Dictionary<string, IOpenApiHeader>(); limitedResponse.Headers["Retry-After"] = new OpenApiHeader { Description = "Seconds until the request may be retried.", Schema = new OpenApiSchema { Type = JsonSchemaType.Integer } }; }
            }
        }

        MarkWriteOnly(document, "CreateAgentEnrollmentTokenResponse", "enrollmentToken");
        MarkWriteOnly(document, "AgentEnrollmentRequest", "enrollmentToken");
        MarkWriteOnly(document, "AgentEnrollmentResponse", "agentCredential");
        MarkWriteOnly(document, "RotateAgentCredentialResponse", "agentCredential");
        if (document.Components?.Schemas is not null)
        {
            foreach (var candidate in document.Components.Schemas.Where(x => ClosedWp03Schemas.Contains(x.Key)))
                if (candidate.Value is OpenApiSchema schema) schema.AdditionalPropertiesAllowed = false;
        }

        return Task.CompletedTask;
    }

    private static void MarkWriteOnly(OpenApiDocument document, string schemaName, string propertyName)
    {
        if (document.Components?.Schemas?.TryGetValue(schemaName, out var schema) == true &&
            schema.Properties?.TryGetValue(propertyName, out var property) == true && property is OpenApiSchema mutable)
            mutable.WriteOnly = true;
    }
}
