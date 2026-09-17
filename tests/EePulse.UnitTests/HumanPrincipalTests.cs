using System.Reflection;
using System.Text;
using EePulse.Domain.Common;
using EePulse.Domain.Identity;

namespace EePulse.UnitTests;

public sealed class HumanPrincipalTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreservesAcceptedIssuerAndSubjectExactly()
    {
        const string issuer = "https://Issuer.Example/Org/Path";
        const string subject = "Subject/βeta/😀";
        var id = Guid.NewGuid();

        var principal = new HumanPrincipal(id, issuer, subject, Now);

        Assert.Equal(id, principal.Id);
        Assert.Equal(issuer, principal.Issuer);
        Assert.Equal(subject, principal.Subject);
        Assert.Equal(Now, principal.CreatedAt);
    }

    [Fact]
    public void AcceptsAstralAndAsciiComponentsWithinTheirScalarLimits()
    {
        var issuer = string.Concat(Enumerable.Repeat("😀", 256));
        var principal = new HumanPrincipal(Guid.NewGuid(), issuer, new string('s', 512), Now);

        Assert.Equal(512, principal.Issuer.Length);
        Assert.Equal(512, principal.Subject.Length);
    }

    [Fact]
    public void AcceptsFiveHundredTwelveAstralUnicodeScalarsForIssuerAndSubject()
    {
        var issuer = string.Concat(Enumerable.Repeat("\U0001F600", 512));
        var subject = string.Concat(Enumerable.Repeat("\U0001F680", 512));

        var principal = new HumanPrincipal(Guid.NewGuid(), issuer, subject, Now);

        Assert.Equal(512, principal.Issuer.EnumerateRunes().Count());
        Assert.Equal(512, principal.Subject.EnumerateRunes().Count());
        Assert.Equal(1_024, principal.Issuer.Length);
        Assert.Equal(1_024, principal.Subject.Length);
        Assert.Equal(issuer, principal.Issuer);
        Assert.Equal(subject, principal.Subject);
    }

    [Fact]
    public void RejectsFiveHundredThirteenAstralUnicodeScalarsForIssuerAndSubject()
    {
        var issuer = string.Concat(Enumerable.Repeat("\U0001F600", 513));
        var subject = string.Concat(Enumerable.Repeat("\U0001F680", 513));

        Assert.Throws<DomainValidationException>(() =>
            new HumanPrincipal(Guid.NewGuid(), issuer, "synthetic-subject", Now));
        Assert.Throws<DomainValidationException>(() =>
            new HumanPrincipal(Guid.NewGuid(), "synthetic-issuer", subject, Now));
    }

    [Fact]
    public void CountsMixedBmpAndAstralInputByUnicodeScalar()
    {
        var issuer = string.Concat(Enumerable.Repeat("a\U0001F600", 256));
        var subject = string.Concat(Enumerable.Repeat("b\U0001F680", 256));

        var principal = new HumanPrincipal(Guid.NewGuid(), issuer, subject, Now);

        Assert.Equal(512, principal.Issuer.EnumerateRunes().Count());
        Assert.Equal(512, principal.Subject.EnumerateRunes().Count());
        Assert.Equal(768, principal.Issuer.Length);
        Assert.Equal(768, principal.Subject.Length);
        Assert.Throws<DomainValidationException>(() =>
            new HumanPrincipal(Guid.NewGuid(), issuer + "a", "synthetic-subject", Now));
        Assert.Throws<DomainValidationException>(() =>
            new HumanPrincipal(Guid.NewGuid(), "synthetic-issuer", subject + "b", Now));
    }

    [Fact]
    public void RejectsEmptyIdAndNonUtcCreationTime()
    {
        Assert.Throws<DomainValidationException>(() => new HumanPrincipal(Guid.Empty, "issuer", "subject", Now));
        Assert.Throws<DomainValidationException>(() => new HumanPrincipal(
            Guid.NewGuid(), "issuer", "subject", Now.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void RejectsInvalidPrincipalComponentsWithoutDisclosingThem()
    {
        string?[] invalid =
        [
            null,
            "",
            " \t ",
            " issuer",
            "issuer ",
            "issuer\tvalue",
            "issuer\u007fvalue",
            "issuer\u2028value",
            new string('i', 513),
            "\ud800",
            "\udc00",
        ];

        foreach (var value in invalid)
        {
            var exception = Assert.Throws<DomainValidationException>(() =>
                new HumanPrincipal(Guid.NewGuid(), value!, "synthetic-subject", Now));
            if (!string.IsNullOrEmpty(value))
                Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
        }

        foreach (var value in invalid)
        {
            var exception = Assert.Throws<DomainValidationException>(() =>
                new HumanPrincipal(Guid.NewGuid(), "synthetic-issuer", value!, Now));
            if (!string.IsNullOrEmpty(value))
                Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CaseDistinctComponentsArePreservedAndNoPublicMutatorsAreExposed()
    {
        var lowercase = new HumanPrincipal(Guid.NewGuid(), "issuer", "subject", Now);
        var uppercase = new HumanPrincipal(Guid.NewGuid(), "Issuer", "Subject", Now);

        Assert.Equal("issuer", lowercase.Issuer);
        Assert.Equal("Issuer", uppercase.Issuer);
        Assert.Equal("subject", lowercase.Subject);
        Assert.Equal("Subject", uppercase.Subject);
        Assert.False(string.Equals(lowercase.Issuer, uppercase.Issuer, StringComparison.Ordinal));
        Assert.False(string.Equals(lowercase.Subject, uppercase.Subject, StringComparison.Ordinal));

        foreach (var property in typeof(HumanPrincipal).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Assert.Null(property.GetSetMethod());
        }

        Assert.DoesNotContain(
            typeof(HumanPrincipal).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);
    }
}
