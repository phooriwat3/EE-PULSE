using System.Net;

namespace EePulse.Domain.Common;

internal static class Guard
{
    public static string Required(string? value, string field, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new DomainValidationException(field, $"{field} is required.");
        }

        if (normalized.Length > maximumLength)
        {
            throw new DomainValidationException(field, $"{field} must not exceed {maximumLength} characters.");
        }

        return normalized;
    }

    public static string? Optional(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new DomainValidationException(field, $"{field} must not exceed {maximumLength} characters.");
        }

        return normalized;
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string field)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainValidationException(field, $"{field} must be UTC.");
        }

        return value;
    }

    public static string Ipv4(string value, string field)
    {
        var normalized = Required(value, field, 45);
        var octets = normalized.Split('.');
        if (octets.Length != 4 || octets.Any(octet =>
                octet.Length == 0 ||
                (octet.Length > 1 && octet[0] == '0') ||
                octet.Any(character => !char.IsAsciiDigit(character))))
        {
            throw new DomainValidationException(field, $"{field} must use unambiguous dotted-decimal IPv4 notation.");
        }

        if (!IPAddress.TryParse(normalized, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new DomainValidationException(field, $"{field} must be a valid IPv4 address.");
        }

        return address.ToString();
    }

    public static string IpAddress(string value, string field)
    {
        var normalized = Required(value, field, 45);
        if (!IPAddress.TryParse(normalized, out var address))
        {
            throw new DomainValidationException(field, $"{field} must be a valid IP address.");
        }

        return address.ToString();
    }

    public static string? Hostname(string? value, string field)
    {
        var normalized = Optional(value, field, 254)?.ToLowerInvariant();
        if (normalized is null)
        {
            return null;
        }

        if (normalized.EndsWith('.'))
        {
            normalized = normalized[..^1];
        }

        var labels = normalized.Split('.');
        if (normalized.Length is 0 or > 253 ||
            IPAddress.TryParse(normalized, out _) ||
            labels.Any(label => label.Length is 0 or > 63 ||
                !char.IsAsciiLetterOrDigit(label[0]) ||
                !char.IsAsciiLetterOrDigit(label[^1]) ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            throw new DomainValidationException(field, $"{field} must be a valid ASCII DNS hostname.");
        }

        return normalized;
    }

    public static int Range(int value, string field, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new DomainValidationException(field, $"{field} must be between {minimum} and {maximum}.");
        }

        return value;
    }
}
