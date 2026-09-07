using EePulse.Domain.Common;
using EePulse.Domain.Preferences;

namespace EePulse.UnitTests;

public sealed class UserTimezonePreferenceDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreationRequiresBoundedPrincipalAndCanonicalTimezoneAtVersionOne()
    {
        var preference = new UserTimezonePreference("https://issuer.example", "subject-001", "Asia/Bangkok", Now);

        Assert.Equal(1, preference.Version);
        Assert.Equal("Asia/Bangkok", preference.Timezone);
        Assert.Equal(Now, preference.CreatedAt);
        Assert.Equal(Now, preference.UpdatedAt);
        Assert.Throws<DomainValidationException>(() => new UserTimezonePreference("issuer", "subject", null!, Now));
        Assert.Throws<DomainValidationException>(() => new UserTimezonePreference("", "subject", "Asia/Bangkok", Now));
        Assert.Throws<DomainValidationException>(() => new UserTimezonePreference("issuer", "", "Asia/Bangkok", Now));
        Assert.Throws<DomainValidationException>(() => new UserTimezonePreference(new string('i', 513), "subject", "Asia/Bangkok", Now));
        Assert.Throws<DomainValidationException>(() => new UserTimezonePreference("issuer", new string('s', 513), "Asia/Bangkok", Now));
    }

    [Fact]
    public void CreationAndMutationsRequireUtcTimestampsAndPreserveCreatedAt()
    {
        Assert.Throws<DomainValidationException>(() => new UserTimezonePreference("issuer", "subject", "Etc/UTC", Now.ToOffset(TimeSpan.FromHours(7))));
        var preference = new UserTimezonePreference("issuer", "subject", "Etc/UTC", Now);
        var createdAt = preference.CreatedAt;
        Assert.Throws<DomainValidationException>(() => preference.ApplyTimezone("Asia/Bangkok", Now.ToOffset(TimeSpan.FromHours(7))));
        Assert.True(preference.ApplyTimezone("Asia/Bangkok", Now.AddMinutes(1)));
        Assert.Equal(createdAt, preference.CreatedAt);
    }

    [Fact]
    public void UpdateClearResetAndNoOpsHaveExactVersionAndTimestampBehavior()
    {
        var preference = new UserTimezonePreference("issuer", "subject", "Etc/UTC", Now);
        Assert.False(preference.ApplyTimezone("Etc/UTC", Now.AddMinutes(1)));
        Assert.Equal(1, preference.Version);
        Assert.Equal(Now, preference.UpdatedAt);

        Assert.True(preference.ApplyTimezone("Asia/Bangkok", Now.AddMinutes(2)));
        Assert.Equal(2, preference.Version);
        Assert.Equal(Now.AddMinutes(2), preference.UpdatedAt);
        Assert.True(preference.ApplyTimezone(null, Now.AddMinutes(3)));
        Assert.Null(preference.Timezone);
        Assert.Equal(3, preference.Version);
        Assert.False(preference.ApplyTimezone(null, Now.AddMinutes(4)));
        Assert.Equal(3, preference.Version);
        Assert.Equal(Now.AddMinutes(3), preference.UpdatedAt);
        Assert.True(preference.ApplyTimezone("Etc/UTC", Now.AddMinutes(5)));
        Assert.Equal(4, preference.Version);
    }

    [Fact]
    public void EarlierStateChangingTimestampsAreRejectedWithoutChangingAnyScalar()
    {
        var preference = new UserTimezonePreference("issuer", "subject", "Etc/UTC", Now);
        Assert.True(preference.ApplyTimezone("Asia/Bangkok", Now.AddMinutes(2)));
        var baseline = State(preference);

        Assert.Throws<DomainValidationException>(() => preference.ApplyTimezone("Asia/Tokyo", Now.AddMinutes(1)));
        AssertState(preference, baseline);
        Assert.Throws<DomainValidationException>(() => preference.ApplyTimezone(null, Now.AddMinutes(1)));
        AssertState(preference, baseline);
        Assert.True(preference.ApplyTimezone(null, Now.AddMinutes(2)));
        var cleared = State(preference);
        Assert.Throws<DomainValidationException>(() => preference.ApplyTimezone("Etc/UTC", Now.AddMinutes(1)));
        AssertState(preference, cleared);
    }

    [Fact]
    public void ActualChangesAtExactlyUpdatedAtAreAllowedAndSameStateIsACompleteNoOp()
    {
        var preference = new UserTimezonePreference("issuer", "subject", "Etc/UTC", Now);
        Assert.True(preference.ApplyTimezone("Asia/Bangkok", Now));
        Assert.Equal(2, preference.Version);
        Assert.Equal(Now, preference.UpdatedAt);
        var changed = State(preference);
        Assert.False(preference.ApplyTimezone("Asia/Bangkok", Now.AddHours(1)));
        AssertState(preference, changed);

        Assert.True(preference.ApplyTimezone(null, Now));
        Assert.Equal(3, preference.Version);
        Assert.Equal(Now, preference.UpdatedAt);
        var cleared = State(preference);
        Assert.False(preference.ApplyTimezone(null, Now.AddHours(1)));
        AssertState(preference, cleared);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData(" Asia/Bangkok")]
    [InlineData("Asia/Bangkok ")]
    [InlineData("Asia\\Bangkok")]
    [InlineData("Not A Zone")]
    public void StorageGuardsRejectOnlyClearlyNonCanonicalTimezoneShapes(string timezone)
    {
        Assert.Throws<DomainValidationException>(() => new UserTimezonePreference("issuer", "subject", timezone, Now));
    }

    private static (string Issuer, string Subject, string? Timezone, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) State(UserTimezonePreference preference) =>
        (preference.Issuer, preference.Subject, preference.Timezone, preference.Version, preference.CreatedAt, preference.UpdatedAt);

    private static void AssertState(UserTimezonePreference preference, (string Issuer, string Subject, string? Timezone, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt) expected) =>
        Assert.Equal(expected, State(preference));
}
