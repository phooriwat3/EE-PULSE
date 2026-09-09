using System.Security.Claims;
using EePulse.Contracts.Agents;
using EePulse.Contracts.Dashboard;

namespace EePulse.Api.Authorization;

public sealed record PrincipalIdentity(string Issuer, string Subject);

public sealed class PrincipalIdentityResolver
{
    public bool TryResolve(ClaimsPrincipal principal, out PrincipalIdentity? identity)
    {
        identity = null;
        if (principal.Identity?.IsAuthenticated != true ||
            string.Equals(principal.Identity.AuthenticationType, AgentContract.CredentialAuthenticationScheme, StringComparison.Ordinal))
        {
            return false;
        }

        var issuers = principal.Claims.Where(claim => claim.Type == "iss").Select(claim => claim.Value).ToArray();
        var subjects = principal.Claims.Where(claim => claim.Type == "sub").Select(claim => claim.Value).ToArray();
        if (issuers.Length != 1 || subjects.Length != 1 ||
            !TimezonePreferenceContract.IsValidPrincipalComponent(issuers[0], TimezonePreferenceContract.MaximumIssuerLength) ||
            !TimezonePreferenceContract.IsValidPrincipalComponent(subjects[0], TimezonePreferenceContract.MaximumSubjectLength) ||
            !HasNoBoundaryWhitespace(issuers[0]) || !HasNoBoundaryWhitespace(subjects[0]))
        {
            return false;
        }

        identity = new PrincipalIdentity(issuers[0], subjects[0]);
        return true;
    }

    private static bool HasNoBoundaryWhitespace(string value) =>
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
