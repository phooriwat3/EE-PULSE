using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Primitives;
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
    public const string DevelopmentIssuer = "https://ee-pulse.invalid/development";
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

        var hasCanonicalActor = TryGetCanonicalPreferenceActor(Request.Headers[ActorHeader], out var actorId);
        var requiresAttribution = roles.Contains("Engineer", StringComparer.Ordinal) ||
            roles.Contains("Administrator", StringComparer.Ordinal);
        if (!hasCanonicalActor)
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
        if (actorId != Guid.Empty)
        {
            claims.Add(new Claim("iss", DevelopmentIssuer));
            claims.Add(new Claim("sub", actorId.ToString("D").ToLowerInvariant()));
        }
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>Parses the only Development actor form that can contribute <c>iss</c>/<c>sub</c> preference identity.</summary>
    public static bool TryGetCanonicalPreferenceActor(StringValues values, out Guid actorId)
    {
        actorId = Guid.Empty;
        if (values.Count != 1) return false;
        var value = values[0];
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            !Guid.TryParseExact(value, "D", out actorId) || actorId == Guid.Empty ||
            !string.Equals(value, actorId.ToString("D"), StringComparison.Ordinal))
        {
            actorId = Guid.Empty;
            return false;
        }
        return true;
    }
}
