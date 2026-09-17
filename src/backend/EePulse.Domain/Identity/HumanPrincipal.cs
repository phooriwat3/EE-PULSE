using System.Buffers;
using System.Globalization;
using System.Text;
using EePulse.Domain.Common;

namespace EePulse.Domain.Identity;

public sealed class HumanPrincipal
{
    public const int MaximumIssuerLength = 512;
    public const int MaximumSubjectLength = 512;

    private HumanPrincipal()
    {
    }

    public HumanPrincipal(Guid id, string issuer, string subject, DateTimeOffset createdAt)
    {
        Id = id == Guid.Empty
            ? throw new DomainValidationException(nameof(Id), "Id is required.")
            : id;
        Issuer = ValidatePrincipalComponent(issuer, nameof(Issuer), MaximumIssuerLength);
        Subject = ValidatePrincipalComponent(subject, nameof(Subject), MaximumSubjectLength);
        CreatedAt = Guard.Utc(createdAt, nameof(CreatedAt));
    }

    public Guid Id { get; private set; }
    public string Issuer { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    private static string ValidatePrincipalComponent(string? value, string field, int maximumLength)
    {
        if (value is null || value.Length is 0 ||
            string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new DomainValidationException(field, $"{field} is invalid.");
        }

        var scalarCount = 0;
        for (var offset = 0; offset < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(offset), out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new DomainValidationException(field, $"{field} is invalid.");
            }

            if (++scalarCount > maximumLength)
            {
                throw new DomainValidationException(field, $"{field} is invalid.");
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                throw new DomainValidationException(field, $"{field} is invalid.");
            }

            offset += consumed;
        }

        return value;
    }
}
