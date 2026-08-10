using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EePulse.Api.Authorization;

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IWebHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevelopmentHeader";
    public const string RoleHeader = "X-EE-Pulse-Role";
    public const string ActorHeader = "X-EE-Pulse-Actor";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment())
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var roles = Request.Headers[RoleHeader]
            .SelectMany(value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roles.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var actor = Request.Headers[ActorHeader].FirstOrDefault();
        var requiresAttribution = roles.Contains("Engineer", StringComparer.Ordinal) ||
            roles.Contains("Administrator", StringComparer.Ordinal);
        if (!Guid.TryParse(actor, out var actorId) || actorId == Guid.Empty)
        {
            if (requiresAttribution)
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    $"{ActorHeader} must contain a non-empty UUID for privileged Development requests."));
            }

            actorId = Guid.Empty;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, actorId.ToString()),
            new(ClaimTypes.Name, "Development user")
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
