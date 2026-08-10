using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EePulse.Api.OpenApi;

public sealed class InventorySecurityDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";

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

        foreach (var path in document.Paths.Where(candidate => candidate.Key.StartsWith("/api/v1/", StringComparison.Ordinal)))
        {
            if (path.Value.Operations is null)
            {
                continue;
            }

            foreach (var operation in path.Value.Operations.Values)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
                });
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentication is required or invalid." });
                operation.Responses.TryAdd("403", new OpenApiResponse { Description = "The authenticated principal lacks the required role or policy." });
            }
        }

        return Task.CompletedTask;
    }
}
