using EePulse.Domain.Common;

namespace EePulse.Domain.Preferences;

public sealed class UserTimezonePreference
{
    public const int MaximumIssuerLength = 512;
    public const int MaximumSubjectLength = 512;
    public const int MaximumTimezoneLength = 255;

    private UserTimezonePreference()
    {
    }

    public UserTimezonePreference(string issuer, string subject, string timezone, DateTimeOffset now)
    {
        Issuer = RequiredPrincipalComponent(issuer, nameof(issuer), MaximumIssuerLength);
        Subject = RequiredPrincipalComponent(subject, nameof(subject), MaximumSubjectLength);
        Timezone = CanonicalTimezone(timezone);
        CreatedAt = Guard.Utc(now, nameof(now));
        UpdatedAt = CreatedAt;
        Version = 1;
    }

    public string Issuer { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string? Timezone { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool ApplyTimezone(string? timezone, DateTimeOffset now)
    {
        var controlledNow = Guard.Utc(now, nameof(now));
        var normalized = timezone is null ? null : CanonicalTimezone(timezone);
        if (string.Equals(Timezone, normalized, StringComparison.Ordinal)) return false;
        if (controlledNow < UpdatedAt)
            throw new DomainValidationException(nameof(now), "now must not be earlier than UpdatedAt.");

        Timezone = normalized;
        UpdatedAt = controlledNow;
        Version = checked(Version + 1);
        return true;
    }

    private static string RequiredPrincipalComponent(string value, string field, int maximumLength)
    {
        var normalized = Guard.Required(value, field, maximumLength);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
            throw new DomainValidationException(field, $"{field} must not have leading or trailing whitespace.");
        return normalized;
    }

    private static string CanonicalTimezone(string value)
    {
        var normalized = Guard.Required(value, nameof(value), MaximumTimezoneLength);
        if (!string.Equals(value, normalized, StringComparison.Ordinal) || !normalized.Contains('/') ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '/' and not '_' and not '-' and not '+'))
            throw new DomainValidationException(nameof(value), "timezone must be a bounded canonical IANA-style identifier.");
        return normalized;
    }
}
