namespace EePulse.Domain.Common;

public sealed class DomainValidationException(string field, string message) : ArgumentException(message, field)
{
    public string Field { get; } = field;
}
