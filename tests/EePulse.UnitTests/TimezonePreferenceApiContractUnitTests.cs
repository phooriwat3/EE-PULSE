using System.Text.Json;
using EePulse.Contracts.Dashboard;

namespace EePulse.UnitTests;

public sealed class TimezonePreferenceApiContractUnitTests
{
    [Fact]
    public void ETagsUseOnlyTheFrozenVersionShapes()
    {
        Assert.Equal("\"tz-0\"", TimezonePreferenceContract.EtagForVersion(0));
        Assert.Equal("\"tz-1\"", TimezonePreferenceContract.EtagForVersion(1));
        Assert.Equal("\"tz-9223372036854775807\"", TimezonePreferenceContract.EtagForVersion(long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimezonePreferenceContract.EtagForVersion(-1));
        Assert.False(TimezonePreferenceContract.IsStrongEtag("W/\"tz-1\""));
        Assert.True(TimezonePreferenceContract.IsStrongEtag("\"tz-01\""));
    }

    [Fact]
    public void TimezoneContractNormalizesAliasesAndRejectsHostOrMalformedValues()
    {
        Assert.True(TimezonePreferenceContract.TryNormalizeTimezone("UTC", out var canonical));
        Assert.Equal("Etc/UTC", canonical);
        Assert.True(TimezonePreferenceContract.TryNormalizeTimezone(null, out canonical));
        Assert.Null(canonical);
        Assert.False(TimezonePreferenceContract.TryNormalizeTimezone("utc", out _));
        Assert.False(TimezonePreferenceContract.TryNormalizeTimezone("Pacific Standard Time", out _));
        Assert.False(TimezonePreferenceContract.TryNormalizeTimezone("../UTC", out _));
    }

    [Fact]
    public void RequestRejectsUnmappedJsonMembers()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TimezonePreferenceRequest>("{\"timezone\":null,\"unexpected\":true}"));
    }

    [Fact]
    public void MutationContractPreservesAbsentNullNoOpAndAdvancesRetainedVersions()
    {
        Assert.Equal(TimezonePreferenceMutationOutcome.NoOpAbsent,
            TimezonePreferenceContract.ClassifyMutation(false, null, null));
        Assert.Equal(TimezonePreferenceMutationOutcome.Create,
            TimezonePreferenceContract.ClassifyMutation(false, null, "UTC"));
        Assert.Equal(TimezonePreferenceMutationOutcome.NoOpCurrent,
            TimezonePreferenceContract.ClassifyMutation(true, "Etc/UTC", "UTC"));
        Assert.Equal(TimezonePreferenceMutationOutcome.Clear,
            TimezonePreferenceContract.ClassifyMutation(true, "Etc/UTC", null));

        var cleared = TimezonePreferenceContract.Advance(new TimezonePreferenceState(true, "Etc/UTC", 1), null);
        Assert.Null(cleared.Timezone);
        Assert.Equal(2, cleared.Version);
        Assert.Equal(new TimezonePreferenceState(true, "Etc/UTC", 1),
            TimezonePreferenceContract.Advance(new TimezonePreferenceState(true, "Etc/UTC", 1), "UTC"));
    }

    [Fact]
    public void ConditionalEtagParsingIsCentralizedAndCommaSafe()
    {
        Assert.True(TimezonePreferenceContract.TryGetIfMatchVersion(["\"tz-1\""], out var version, out var match));
        Assert.Equal(1, version);
        Assert.Equal(TimezoneIfMatchClassification.Current, match);
        Assert.False(TimezonePreferenceContract.TryGetIfMatchVersion(["W/\"tz-1\""], out _, out match));
        Assert.Equal(TimezoneIfMatchClassification.Invalid, match);
        Assert.False(TimezonePreferenceContract.TryGetIfMatchVersion(["\"tz-1\", \"tz-2\""], out _, out match));
        Assert.Equal(TimezoneIfMatchClassification.Invalid, match);

        Assert.Equal(TimezoneIfNoneMatchClassification.Match,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["W/\"tz-1\""], "\"tz-1\"", true));
        Assert.Equal(TimezoneIfNoneMatchClassification.Match,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["\"opaque,tag\", \"tz-1\""], "\"tz-1\"", true));
        Assert.Equal(TimezoneIfNoneMatchClassification.NoMatch,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["\"opaque,tag\""], "\"tz-1\"", true));
        Assert.Equal(TimezoneIfNoneMatchClassification.Match,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["*"], "\"tz-1\"", true));
        Assert.Equal(TimezoneIfNoneMatchClassification.NoMatch,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["*"], "\"tz-0\"", false));
        Assert.Equal(TimezoneIfNoneMatchClassification.Invalid,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["*, \"tz-1\""], "\"tz-1\"", true));
        Assert.Equal(TimezoneIfNoneMatchClassification.Invalid,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["\"tz-1\",,"], "\"tz-1\"", true));
        Assert.Equal(TimezoneIfNoneMatchClassification.Invalid,
            TimezonePreferenceContract.ClassifyIfNoneMatch(["W/garbage"], "\"tz-1\"", true));
    }
}
