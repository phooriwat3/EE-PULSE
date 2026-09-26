using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EePulse.Api.Authorization;
using EePulse.Api.Dashboard;
using EePulse.Application.Time;
using EePulse.Contracts.Dashboard;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EePulse.UnitTests;

public sealed class Wp07IncidentRuntimeTests
{
    private static readonly Guid IncidentId = Guid.ParseExact("01234567-89ab-cdef-0123-456789abcdef", "D");
    private static readonly Guid ActorId = Guid.ParseExact("fedcba98-7654-3210-fedc-ba9876543210", "D");
    private static readonly string[] IncidentCommandResultCaseNames =
    [
        "DependencyFailure", "FreshPreconditionFailed", "FreshStateConflict", "FreshSuccess",
        "IdempotencyReuseConflict", "IndeterminateCommit", "NotFound", "Replay"
    ];
    private const string CorrelationId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task RuntimeEntitiesHaveFrozenImmutableEfMappings()
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>()
            .UseNpgsql("Host=localhost;Database=wp07_runtime_model;Username=postgres;Password=postgres")
            .Options;
        await using var db = new EePulseDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;

        AssertEntity(model.FindEntityType(typeof(IncidentComment))!, "incident_comments",
            nameof(IncidentComment.Id), "id");
        AssertEntity(model.FindEntityType(typeof(IncidentLifecycleAction))!, "incident_lifecycle_actions",
            nameof(IncidentLifecycleAction.EventId), "event_id");
        var receipt = model.FindEntityType(typeof(IdempotencyReceipt))!;
        AssertEntity(receipt, "idempotency_receipts", nameof(IdempotencyReceipt.IdempotencyKey), "idempotency_key");
        Assert.Equal(typeof(Guid?), receipt.FindProperty(nameof(IdempotencyReceipt.IncidentCommentId))!.ClrType);
        Assert.Equal(typeof(Guid?), receipt.FindProperty(nameof(IdempotencyReceipt.IncidentLifecycleActionId))!.ClrType);
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() == "uq_idempotency_receipts_incident_comment");
        Assert.Contains(receipt.GetIndexes(), index => index.IsUnique &&
            index.GetDatabaseName() == "uq_idempotency_receipts_lifecycle_action");
    }

    private static void AssertEntity(IReadOnlyEntityType entity, string table, string keyProperty, string column)
    {
        Assert.Equal(table, entity.GetTableName());
        Assert.Equal(column, entity.FindProperty(keyProperty)!.GetColumnName(StoreObjectIdentifier.Table(table, null)));
        Assert.All(entity.GetProperties(), property =>
            Assert.Equal(PropertySaveBehavior.Throw, property.GetAfterSaveBehavior()));
    }

    [Fact]
    public void RequestFingerprintV1MatchesAdrKnownVector()
    {
        var digest = IncidentRequestFingerprintV1.Compute(
            IncidentRequestFingerprintV1.Version,
            IncidentCommandRoute.AddComment,
            IncidentId,
            ActorId,
            "\"e\"",
            Encoding.UTF8.GetBytes("{\"Comment\":\"x\"}"));

        Assert.Equal("937CF17541DFE9365E1CCE7039A105AF09E3435E6FBE0518E6A38A436543E608", Convert.ToHexString(digest.AsSpan()));
    }

    [Fact]
    public void RequestFingerprintV1PreservesExtendedOpaqueIfMatchOctetsWithoutReplacement()
    {
        const string extendedIfMatch = "\"\u0080\u00ff\"";
        var body = "{\"Comment\":\"x\"}"u8.ToArray();
        var canonical = IncidentRequestFingerprintV1.CreateCanonicalBytes(
            IncidentCommandRoute.AddComment, IncidentId, ActorId, extendedIfMatch, body);
        var extendedDigest = IncidentRequestFingerprintV1.Compute(
            IncidentRequestFingerprintV1.Version, IncidentCommandRoute.AddComment, IncidentId, ActorId, extendedIfMatch, body);
        var questionDigest = IncidentRequestFingerprintV1.Compute(
            IncidentRequestFingerprintV1.Version, IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"?\"", body);

        Assert.Equal("000000042280FF22", Convert.ToHexString(FieldWithPrefix(canonical, 4)));
        Assert.Equal("1B271A4CDC2F26C25943D526738AF69C1750BB558A1FD6893475D16F791227E0", Convert.ToHexString(extendedDigest.AsSpan()));
        Assert.NotEqual(extendedDigest, questionDigest);
    }

    [Fact]
    public void RequestFingerprintV1ValidatesTheFrozenOpaqueIfMatchCharacterRange()
    {
        var body = "{\"Comment\":\"x\"}"u8.ToArray();

        Assert.Equal(32, IncidentRequestFingerprintV1.Compute(1, IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"\"", body).Length);
        Assert.Throws<ArgumentException>(() => IncidentRequestFingerprintV1.Compute(1, IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"\u0100\"", body));
        Assert.Throws<ArgumentException>(() => IncidentRequestFingerprintV1.Compute(1, IncidentCommandRoute.AddComment, IncidentId, ActorId, "W/\"e\"", body));
        Assert.Throws<ArgumentException>(() => IncidentRequestFingerprintV1.Compute(1, IncidentCommandRoute.AddComment, IncidentId, ActorId, "e", body));
        Assert.Throws<ArgumentException>(() => IncidentRequestFingerprintV1.Compute(1, IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"a\u0001b\"", body));
        Assert.Throws<ArgumentException>(() => IncidentRequestFingerprintV1.Compute(1, IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"a\u007fb\"", body));
    }

    [Fact]
    public void RequestFingerprintV1UsesSixU32BigEndianFieldsInFrozenOrder()
    {
        var body = Encoding.UTF8.GetBytes("{\"Comment\":\"x\"}");
        var canonical = IncidentRequestFingerprintV1.CreateCanonicalBytes(
            IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"e\"", body);

        var offset = IncidentRequestFingerprintV1.Marker.Length;
        Assert.Equal(0, canonical[offset++]);
        AssertField(canonical, ref offset, "POST"u8);
        AssertField(canonical, ref offset, "/api/v1/incidents/{id}/comments"u8);
        AssertField(canonical, ref offset, "01234567-89ab-cdef-0123-456789abcdef"u8);
        AssertField(canonical, ref offset, "fedcba98-7654-3210-fedc-ba9876543210"u8);
        AssertField(canonical, ref offset, "\"e\""u8);
        AssertField(canonical, ref offset, body);
        Assert.Equal(canonical.Length, offset);
    }

    [Fact]
    public void RequestFingerprintV1PreservesQuoteBackslashAndSupplementaryUtf8BodyBytes()
    {
        var quotedAndSlashed = "{\"Comment\":\"a\\\\\\\"b\\\\\\\\c\"}"u8.ToArray();
        var supplementary = "{\"Comment\":\"\U0001F600\"}"u8.ToArray();
        var quoteCanonical = IncidentRequestFingerprintV1.CreateCanonicalBytes(IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"e\"", quotedAndSlashed);
        var supplementaryCanonical = IncidentRequestFingerprintV1.CreateCanonicalBytes(IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"e\"", supplementary);

        Assert.Equal(quotedAndSlashed, LastField(quoteCanonical));
        Assert.Equal(supplementary, LastField(supplementaryCanonical));
        Assert.Contains((byte)0xF0, LastField(supplementaryCanonical));
        Assert.NotEqual(IncidentRequestFingerprintV1.Compute(1, IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"e\"", quotedAndSlashed),
            IncidentRequestFingerprintV1.Compute(1, IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"e\"", supplementary));
    }

    [Fact]
    public void CanonicalCommandBodyUsesAdr013ShortestUtf8JEncoding()
    {
        var text = "ASCII-\u00e9-\U0001F600-\uFFFD-<\"\\";
        var comment = IncidentEndpoints.CanonicalCommandBody(IncidentCommandRoute.AddComment, text);
        var acknowledgement = IncidentEndpoints.CanonicalCommandBody(IncidentCommandRoute.Acknowledge, text);
        var resolution = IncidentEndpoints.CanonicalCommandBody(IncidentCommandRoute.Resolve, text);

        Assert.Equal("{\"Comment\":\"ASCII-\u00e9-\U0001F600-\uFFFD-<\\\"\\\\\"}", Encoding.UTF8.GetString(comment));
        Assert.Equal(comment, acknowledgement);
        Assert.Equal("{\"Note\":\"ASCII-\u00e9-\U0001F600-\uFFFD-<\\\"\\\\\"}", Encoding.UTF8.GetString(resolution));
        Assert.Contains((byte)0xc3, comment);
        Assert.Contains((byte)0xa9, comment);
        Assert.Contains((byte)0xf0, comment);
        Assert.Contains((byte)0x9f, comment);
        Assert.Contains((byte)0x98, comment);
        Assert.Contains((byte)0x80, comment);
        Assert.Contains((byte)0xef, comment);
        Assert.Contains((byte)0xbf, comment);
        Assert.Contains((byte)0xbd, comment);
        Assert.Contains((byte)'<', comment);
        Assert.DoesNotContain("\\u00E9"u8.ToArray(), comment);
        Assert.DoesNotContain("\\u003C"u8.ToArray(), comment);
        Assert.Throws<ArgumentException>(() => IncidentEndpoints.CanonicalCommandBody(IncidentCommandRoute.AddComment,
            new string((char)0xd800, 1)));
    }

    [Fact]
    public void RequestFingerprintV1ExcludesIdempotencyKey()
    {
        var body = "{\"Comment\":\"x\"}"u8.ToArray();
        var first = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "01234567-89ab-cdef-0123-456789abcdef", new PrincipalIdentity("issuer", "subject"), "\"e\"", body, "0123456789abcdef0123456789abcdef", "x");
        var second = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "fedcba98-7654-3210-fedc-ba9876543210", new PrincipalIdentity("issuer", "subject"), "\"e\"", body, "0123456789abcdef0123456789abcdef", "x");
        var firstDigest = IncidentRequestFingerprintV1.Compute(1, first.Route, first.IncidentId, ActorId, first.ParsedStrongIfMatch, first.CanonicalRequestBodyUtf8.AsSpan());
        var secondDigest = IncidentRequestFingerprintV1.Compute(1, second.Route, second.IncidentId, ActorId, second.ParsedStrongIfMatch, second.CanonicalRequestBodyUtf8.AsSpan());

        Assert.Equal(firstDigest, secondDigest);
    }

    [Fact]
    public void RequestFingerprintV1RejectsUnsupportedVersionsAndOversizedLengths()
    {
        Assert.Throws<NotSupportedException>(() => IncidentRequestFingerprintV1.Compute(2, IncidentCommandRoute.AddComment, IncidentId, ActorId, "\"e\"", "{}"u8));
        Assert.Throws<OverflowException>(() => IncidentRequestFingerprintV1.CheckedU32Length((long)uint.MaxValue + 1));
    }

    [Theory]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", 969806125)]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", -692621145)]
    public void AdvisoryKeyDerivationUsesFrozenNamespaceAndSignedBigEndianResult(string idempotencyKey, int expectedDerivedKey)
    {
        var derived = IncidentIdempotencyAdvisoryKeyDeriver.Derive(idempotencyKey);

        Assert.Equal(1464873015, derived.NamespaceKey);
        Assert.Equal(expectedDerivedKey, derived.DerivedKey);
    }

    [Fact]
    public void AdvisoryKeyDerivationIsDeterministicAndKeepsFullKeySeparateFromLockPair()
    {
        const string firstKey = "01234567-89ab-cdef-0123-456789abcdef";
        const string secondKey = "00000000-0000-0000-0000-000000000000";

        var first = IncidentIdempotencyAdvisoryKeyDeriver.Derive(firstKey);
        var repeated = IncidentIdempotencyAdvisoryKeyDeriver.Derive(firstKey);
        var second = IncidentIdempotencyAdvisoryKeyDeriver.Derive(secondKey);

        Assert.Equal(first, repeated);
        Assert.NotEqual(firstKey, secondKey);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(typeof(IncidentIdempotencyAdvisoryKeyDeriver).GetMembers(), member =>
            member.Name.Contains("IdempotencyKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", 1,
        "45452D50554C53452F575030372F494E434944454E542D455441472F5631000000002430313233343536372D383961622D636465662D303132332D3435363738396162636465660000000131",
        "\"wp07-2b1-sha256-e1xxhgyKZ7JHvdyQt-Y1IoMvWYvWJ7pEFW_XeRWieGw\"")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", 123456789,
        "45452D50554C53452F575030372F494E434944454E542D455441472F5631000000002430313233343536372D383961622D636465662D303132332D34353637383961626364656600000009313233343536373839",
        "\"wp07-2b1-sha256-sWxTrABTqCuzsa7GPnZHknCJ1-ee2Tt1QmsgX-zJdyc\"")]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff", long.MaxValue,
        "45452D50554C53452F575030372F494E434944454E542D455441472F5631000000002430303131323233332D343435352D363637372D383839392D6161626263636464656566660000001339323233333732303336383534373735383037",
        "\"wp07-2b1-sha256-VKbTMA15-zXRItBaPc5pvWeM9B1BHRP941Rv72oEmwk\"")]
    [InlineData("ffeeddcc-bbaa-9988-7766-554433221100", 42,
        "45452D50554C53452F575030372F494E434944454E542D455441472F5631000000002466666565646463632D626261612D393938382D373736362D353534343333323231313030000000023432",
        "\"wp07-2b1-sha256-PpWE8RjSnp1jO2itMeAuOV149N-YZWQeFxbMy7xNC6U\"")]
    public void IncidentEtagV1MatchesAllFrozenVectors(string incidentId, long rowVersion, string canonicalHex, string expectedEtag)
    {
        var provider = new IncidentEtagV1();
        var parsed = Guid.ParseExact(incidentId, "D");

        Assert.Equal(canonicalHex, Convert.ToHexString(IncidentEtagV1.CreateCanonicalBytes(parsed, rowVersion).AsSpan()));
        Assert.Equal(expectedEtag, provider.Create(parsed, rowVersion));
    }

    [Fact]
    public void IncidentEtagV1RejectsEmptyGuidAndNonPositiveVersionsAndAcceptsInt64Maximum()
    {
        var provider = new IncidentEtagV1();

        Assert.Throws<ArgumentException>(() => provider.Create(Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.Create(IncidentId, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.Create(IncidentId, -1));
        Assert.StartsWith("\"wp07-2b1-sha256-", provider.Create(IncidentId, long.MaxValue), StringComparison.Ordinal);
    }

    [Fact]
    public void CursorKeyRingParsesOnlyTheFrozenActiveAndRetainedKeyGrammar()
    {
        var active = RandomNumberGenerator.GetBytes(32);
        var retained = RandomNumberGenerator.GetBytes(32);
        var ring = IncidentCursorKeyRing.Parse("active-1", $"active-1={Convert.ToBase64String(active)};retained.2={Convert.ToBase64String(retained)}");

        Assert.Equal("active-1", ring.ActiveKeyId);
        Assert.Equal(active, ring.ActiveKey.ToArray());
        Assert.True(ring.TryGetKey("retained.2", out var retainedKey));
        Assert.Equal(retained, retainedKey.ToArray());
        Assert.False(ring.TryGetKey("missing", out _));
    }

    [Theory]
    [MemberData(nameof(InvalidCursorKeyRings))]
    public void CursorKeyRingRejectsMissingMalformedAndFallbackConfiguration(string? activeKeyId, string? keyRing)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => IncidentCursorKeyRing.Parse(activeKeyId, keyRing));
        Assert.Equal(IncidentCursorKeyRing.InvalidConfigurationMessage, exception.Message);
    }

    public static IEnumerable<object?[]> InvalidCursorKeyRings()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        yield return [null, $"active={key}"];
        yield return ["", $"active={key}"];
        yield return ["active", null];
        yield return ["active", ""];
        foreach (var entry in new[]
        {
            $"active={key} ", $"active={key};active={key}", $"active={key[..^1]}",
            $"active={key[..^2]}-=", $"other={key}", $"active=={key}", $"={key}",
            $"active{key}", $"active={key};", $";active={key}", $"active={key};;other={key}",
            $"active={Convert.ToBase64String(RandomNumberGenerator.GetBytes(31))}",
            $"active={Convert.ToBase64String(RandomNumberGenerator.GetBytes(33))}"
        }) yield return ["active", entry];
        yield return ["-active", $"-active={key}"];
        yield return ["Active", $"active={key}"];
        // RFC 4648 requires unused padding bits to be zero.
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var noncanonical = key[..^2] + alphabet[alphabet.IndexOf(key[^2], StringComparison.Ordinal) | 1] + "=";
        yield return ["active", $"active={noncanonical}"];
    }

    [Fact]
    public void CursorProtectionBindsFiltersAndSurvivesRestartAndTwoPhaseRotation()
    {
        var oldKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var oldRing = IncidentCursorKeyRing.Parse("old", $"old={oldKey}");
        var distributed = IncidentCursorKeyRing.Parse("old", $"old={oldKey};new={newKey}");
        var rotated = IncidentCursorKeyRing.Parse("new", $"old={oldKey};new={newKey}");
        var retired = IncidentCursorKeyRing.Parse("new", $"new={newKey}");
        var clock = new CursorClock(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        var binding = new IncidentCursorBinding("incidents", null,
            new IncidentListFilter(null, null, null, null, null, null, IncidentSort.OpenedAtDesc), 1);
        var seek = new IncidentSeek(clock.UtcNow, IncidentId);
        var token = new IncidentCursorProtector(oldRing, clock).Issue(binding, seek);
        foreach (var ring in new[] { oldRing, distributed, rotated })
        {
            Assert.Equal(IncidentCursorValidation.Valid, new IncidentCursorProtector(ring, clock).Validate(token, binding, out var actual));
            Assert.Equal(seek, actual);
        }
        var verifier = new IncidentCursorProtector(rotated, clock);
        Assert.Equal(IncidentCursorValidation.FilterMismatch, verifier.Validate(token, binding with { Route = "device-incidents" }, out _));
        Assert.Equal(IncidentCursorValidation.FilterMismatch, verifier.Validate(token,
            binding with { Filter = binding.Filter! with { Status = IncidentStatus.Open } }, out _));
        Assert.Equal(IncidentCursorValidation.Invalid, verifier.Validate("!" + token, binding, out _));
        var tampered = (token[0] == 'A' ? "B" : "A") + token[1..];
        Assert.Equal(IncidentCursorValidation.Invalid, verifier.Validate(tampered, binding, out _));
        var newer = new IncidentCursorProtector(rotated, clock).Issue(binding, seek);
        Assert.Equal(IncidentCursorValidation.Valid, new IncidentCursorProtector(distributed, clock).Validate(newer, binding, out _));
        clock.UtcNow = clock.UtcNow.AddMilliseconds(-1);
        Assert.Equal(IncidentCursorValidation.Invalid, verifier.Validate(token, binding, out _));
        clock.UtcNow = clock.UtcNow.AddMilliseconds(IncidentCursorProtector.LifetimeMilliseconds);
        Assert.Equal(IncidentCursorValidation.Valid, verifier.Validate(token, binding, out _));
        clock.UtcNow = clock.UtcNow.AddMilliseconds(1);
        Assert.Equal(IncidentCursorValidation.Invalid, verifier.Validate(token, binding, out _));
        clock.UtcNow = clock.UtcNow.AddMinutes(10);
        Assert.Equal(IncidentCursorValidation.Invalid, new IncidentCursorProtector(retired, clock).Validate(token, binding, out _));
        Assert.False(IncidentCursorProtector.IsCurrent(long.MaxValue, long.MaxValue));
    }

    [Fact]
    public void CursorProtectionBindsTheExactCommentSortToken()
    {
        var ring = IncidentCursorKeyRing.Parse("test", "test=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var clock = new CursorClock(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        var descending = new IncidentCursorBinding("incident-comments", IncidentId.ToString("D"), null,
            Wp07DashboardContract.SchemaVersion, "CreatedAtDesc");
        var token = new IncidentCursorProtector(ring, clock).Issue(descending, new IncidentSeek(clock.UtcNow, IncidentId));

        var protector = new IncidentCursorProtector(ring, clock);
        Assert.Equal(IncidentCursorValidation.Valid, protector.Validate(token, descending, out var seek));
        Assert.Equal(new IncidentSeek(clock.UtcNow, IncidentId), seek);
        Assert.Equal(IncidentCursorValidation.FilterMismatch,
            protector.Validate(token, descending with { Sort = "CreatedAtAsc" }, out _));
    }

    private sealed class CursorClock(DateTimeOffset now) : IUtcClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public void CursorRejectsAuthenticatedInvalidVersionKeyExpiryAndOverflowUniformly()
    {
        var ring = IncidentCursorKeyRing.Parse("test", "test=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var clock = new CursorClock(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));
        var binding = new IncidentCursorBinding("incidents", null,
            new IncidentListFilter(null, null, null, null, null, null, IncidentSort.OpenedAtDesc), 1);
        var protector = new IncidentCursorProtector(ring, clock);
        var token = protector.Issue(binding, new IncidentSeek(clock.UtcNow, IncidentId));
        var decoded = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + new string('=', (4 - token.Length % 4) % 4));
        foreach (var change in new Action<System.Text.Json.Nodes.JsonNode>[]
        {
            node => node["Version"] = 2,
            node => node["Kid"] = "unknown",
            node => node["IssuedAtMs"] = clock.UtcNow.ToUnixTimeMilliseconds() - IncidentCursorProtector.LifetimeMilliseconds,
            node => node["IssuedAtMs"] = clock.UtcNow.ToUnixTimeMilliseconds() + 1,
            node => node["IssuedAtMs"] = long.MaxValue,
            node => node["Seek"] = null
        })
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(decoded.AsSpan(0, decoded.Length - 32))!;
            change(node);
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node);
            var signed = payload.Concat(HMACSHA256.HashData(ring.ActiveKey.AsSpan(), payload)).ToArray();
            var invalid = Convert.ToBase64String(signed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(IncidentCursorValidation.Invalid, protector.Validate(invalid, binding, out var seek));
            Assert.Null(seek);
        }
    }

    [Theory]
    [InlineData("?unknown=1")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=201")]
    [InlineData("?pageSize=-1")]
    [InlineData("?pageSize=1&pageSize=2")]
    [InlineData("?cursor=a&cursor=b")]
    [InlineData("?cursor=")]
    [InlineData("?status=open")]
    [InlineData("?status=0")]
    [InlineData("?sort=0")]
    [InlineData("?openedFrom=2026-09-23")]
    [InlineData("?openedFrom=2026-09-23T00:00:00.Z")]
    [InlineData("?openedFrom=2026-09-23T00:00:00Z&openedTo=2026-09-22T00:00:00Z")]
    [InlineData("?siteId=bad")]
    [InlineData("?Status=Open")]
    public void IncidentQueryRejectsInvalidAndRepeatedValues(string query)
    {
        var values = new Microsoft.AspNetCore.Http.QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));
        Assert.False(IncidentEndpoints.TryQuery(values, false, out _, out _, out _));
    }

    [Fact]
    public void DeviceIncidentQueryRejectsListOnlyFiltersAndAcceptsInclusiveUtcBounds()
    {
        var values = new Microsoft.AspNetCore.Http.QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?siteId=" + IncidentId.ToString("D")));
        Assert.False(IncidentEndpoints.TryQuery(values, true, out _, out _, out _));
        values = new Microsoft.AspNetCore.Http.QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
            "?openedFrom=2026-09-22T00:00:00Z&openedTo=2026-09-22T00:00:00.0000000Z&sort=OpenedAtAsc&pageSize=200"));
        Assert.True(IncidentEndpoints.TryQuery(values, true, out var filter, out var cursor, out var size));
        Assert.Equal(filter!.OpenedFrom, filter.OpenedTo);
        Assert.Equal(IncidentSort.OpenedAtAsc, filter.Sort);
        Assert.Null(cursor);
        Assert.Equal(200, size);
    }

    [Fact]
    public void IncidentCommandInputDirectlyEnforcesOwnedInvariantsAndImmutableState()
    {
        var commandBytes = "{\"Comment\":\"x\"}"u8.ToArray();
        ImmutableArray<byte> defaultCommandBytes = default;
        var command = new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "01234567-89ab-cdef-0123-456789abcdef", new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, "0123456789abcdef0123456789abcdef", "x");

        commandBytes[0] = (byte)'!';
        var retrievedBytes = command.CanonicalRequestBodyUtf8.ToArray();
        retrievedBytes[0] = (byte)'!';

        Assert.Equal((byte)'{', command.CanonicalRequestBodyUtf8[0]);
        Assert.False(command.CanonicalRequestBodyUtf8.IsDefault);
        AssertGetOnlyNonRecordState(typeof(IncidentCommandInput<>),
            nameof(IncidentCommandInput<string>.Route), nameof(IncidentCommandInput<string>.IncidentId),
            nameof(IncidentCommandInput<string>.IdempotencyKey), nameof(IncidentCommandInput<string>.Identity),
            nameof(IncidentCommandInput<string>.ParsedStrongIfMatch), nameof(IncidentCommandInput<string>.CanonicalRequestBodyUtf8),
            nameof(IncidentCommandInput<string>.TrustedCorrelationId), nameof(IncidentCommandInput<string>.Command));
        Assert.Equal(typeof(ImmutableArray<byte>), typeof(IncidentCommandInput<string>)
            .GetProperty(nameof(IncidentCommandInput<string>.CanonicalRequestBodyUtf8))!.PropertyType);

        Assert.Throws<ArgumentOutOfRangeException>(() => new IncidentCommandInput<string>((IncidentCommandRoute)99, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, CorrelationId, "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, Guid.Empty,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, CorrelationId, "x"));
        Assert.Throws<ArgumentNullException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            null!, new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, CorrelationId, "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            string.Empty, new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, CorrelationId, "x"));
        Assert.Throws<ArgumentNullException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", null!, "\"e\"", commandBytes, CorrelationId, "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "W/\"e\"", commandBytes, CorrelationId, "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "e", commandBytes, CorrelationId, "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"a\u0001b\"", commandBytes, CorrelationId, "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", Array.Empty<byte>(), CorrelationId, "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", defaultCommandBytes.AsSpan(), CorrelationId, "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, " ", "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, CorrelationId.ToUpperInvariant(), "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, "01234567-89ab-cdef-0123-456789abcdef", "x"));
        Assert.Throws<ArgumentException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, "not-a-correlation-id", "x"));
        Assert.Throws<ArgumentNullException>(() => new IncidentCommandInput<string>(IncidentCommandRoute.AddComment, IncidentId,
            "key", new PrincipalIdentity("issuer", "subject"), "\"e\"", commandBytes, CorrelationId, null!));
    }

    [Fact]
    public void StoredIncidentHttpResponseDirectlyEnforcesOwnedInvariantsAndImmutableState()
    {
        var responseBytes = "{\"id\":\"x\"}"u8.ToArray();
        ImmutableArray<byte> defaultResponseBytes = default;
        var response = new StoredIncidentHttpResponse(200, responseBytes, "application/json; charset=utf-8", "\"strong\"");

        responseBytes[0] = (byte)'!';
        var retrievedBytes = response.BodyUtf8.ToArray();
        retrievedBytes[0] = (byte)'!';

        Assert.Equal(200, response.StatusCode);
        Assert.Equal((byte)'{', response.BodyUtf8[0]);
        Assert.False(response.BodyUtf8.IsDefault);
        AssertGetOnlyNonRecordState(typeof(StoredIncidentHttpResponse),
            nameof(StoredIncidentHttpResponse.StatusCode), nameof(StoredIncidentHttpResponse.BodyUtf8),
            nameof(StoredIncidentHttpResponse.ContentType), nameof(StoredIncidentHttpResponse.Etag));
        Assert.Equal(typeof(ImmutableArray<byte>), typeof(StoredIncidentHttpResponse)
            .GetProperty(nameof(StoredIncidentHttpResponse.BodyUtf8))!.PropertyType);

        Assert.Throws<ArgumentOutOfRangeException>(() => new StoredIncidentHttpResponse(202, responseBytes, "application/json", "\"strong\""));
        Assert.Throws<ArgumentException>(() => new StoredIncidentHttpResponse(200, Array.Empty<byte>(), "application/json", "\"strong\""));
        Assert.Throws<ArgumentException>(() => new StoredIncidentHttpResponse(200, defaultResponseBytes.AsSpan(), "application/json", "\"strong\""));
        Assert.Throws<ArgumentNullException>(() => new StoredIncidentHttpResponse(200, responseBytes, null!, "\"strong\""));
        Assert.Throws<ArgumentException>(() => new StoredIncidentHttpResponse(200, responseBytes, string.Empty, "\"strong\""));
        Assert.Throws<ArgumentException>(() => new StoredIncidentHttpResponse(200, responseBytes, "application/json", "W/\"strong\""));
        Assert.Throws<ArgumentException>(() => new StoredIncidentHttpResponse(200, responseBytes, "application/json", "strong"));
        Assert.Throws<ArgumentException>(() => new StoredIncidentHttpResponse(200, responseBytes, "application/json", "\"a\u0001b\""));
        Assert.Throws<ArgumentException>(() => new StoredIncidentHttpResponse(200, responseBytes, "application/json", "\"a\u007fb\""));
        Assert.Throws<ArgumentException>(() => new StoredIncidentHttpResponse(200, responseBytes, "application/json", "\"\u0100\""));
    }

    [Fact]
    public void LockedIncidentExecutionContextDirectlyEnforcesOwnedInvariantsAndCapabilityBoundary()
    {
        var occurredAt = new DateTimeOffset(2026, 9, 21, 1, 2, 3, TimeSpan.Zero);
        var probeId = Guid.NewGuid();
        var incident = new AvailabilityIncident(IncidentId, probeId, occurredAt);
        var projection = new ProbeStatusProjection(probeId, ProbeStatus.Unknown, 0, 0, null, null, null, null);
        var context = new LockedIncidentExecutionContext<string>("command", incident, projection, ActorId,
            "01234567-89ab-cdef-0123-456789abcdef", CorrelationId, occurredAt);

        Assert.Same(incident, context.Incident);
        Assert.Same(projection, context.Projection);
        AssertGetOnlyNonRecordState(typeof(LockedIncidentExecutionContext<>),
            nameof(LockedIncidentExecutionContext<string>.Command), nameof(LockedIncidentExecutionContext<string>.Incident),
            nameof(LockedIncidentExecutionContext<string>.Projection), nameof(LockedIncidentExecutionContext<string>.ActorId),
            nameof(LockedIncidentExecutionContext<string>.IdempotencyKey), nameof(LockedIncidentExecutionContext<string>.TrustedCorrelationId),
            nameof(LockedIncidentExecutionContext<string>.OccurredAt));

        var members = typeof(LockedIncidentExecutionContext<string>).GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forbidden = new[] { "DbContext", "Transaction", "SaveChanges", "Save", "Commit", "Rollback", "Receipt", "Lock" };
        Assert.DoesNotContain(members, member => forbidden.Any(token => member.Name.Contains(token, StringComparison.Ordinal)));

        Assert.Throws<ArgumentNullException>(() => new LockedIncidentExecutionContext<string>(null!, incident, projection, ActorId, "key", CorrelationId, occurredAt));
        Assert.Throws<ArgumentNullException>(() => new LockedIncidentExecutionContext<string>("command", null!, projection, ActorId, "key", CorrelationId, occurredAt));
        Assert.Throws<ArgumentNullException>(() => new LockedIncidentExecutionContext<string>("command", incident, null!, ActorId, "key", CorrelationId, occurredAt));
        Assert.Throws<ArgumentException>(() => new LockedIncidentExecutionContext<string>("command", incident, projection, Guid.Empty, "key", CorrelationId, occurredAt));
        Assert.Throws<ArgumentNullException>(() => new LockedIncidentExecutionContext<string>("command", incident, projection, ActorId, null!, CorrelationId, occurredAt));
        Assert.Throws<ArgumentException>(() => new LockedIncidentExecutionContext<string>("command", incident, projection, ActorId, string.Empty, CorrelationId, occurredAt));
        Assert.Throws<ArgumentException>(() => new LockedIncidentExecutionContext<string>("command", incident, projection, ActorId, "key", " ", occurredAt));
        Assert.Throws<ArgumentException>(() => new LockedIncidentExecutionContext<string>("command", incident, projection, ActorId, "key", CorrelationId.ToUpperInvariant(), occurredAt));
        Assert.Throws<ArgumentException>(() => new LockedIncidentExecutionContext<string>("command", incident, projection, ActorId, "key", "01234567-89ab-cdef-0123-456789abcdef", occurredAt));
        Assert.Throws<ArgumentException>(() => new LockedIncidentExecutionContext<string>("command", incident, projection, ActorId, "key", "not-a-correlation-id", occurredAt));
        Assert.Throws<ArgumentException>(() => new LockedIncidentExecutionContext<string>("command", incident, projection, ActorId, "key", CorrelationId, occurredAt.ToOffset(TimeSpan.FromHours(7))));
    }

    [Fact]
    public void BoundaryTypesRejectInvalidConstructionAndExposeNoMutationPath()
    {
        var completedAt = new DateTimeOffset(2026, 9, 21, 1, 2, 3, TimeSpan.Zero);
        var response = new StoredIncidentHttpResponse(200, "{\"id\":\"x\"}"u8, "application/json; charset=utf-8", "\"strong\"");
        var context = new IncidentCommandResponseContext(IncidentId, "\"strong\"", CorrelationId, completedAt);
        var outcome = new StagedCommandOutcome(Guid.NewGuid(), "acknowledgement", completedAt);

        Assert.Equal(IncidentId, context.IncidentId);
        Assert.Equal("acknowledgement", outcome.OutcomeKind);
        Assert.NotNull(new IncidentCommandResult<string>.Replay(response));
        Assert.NotNull(new IncidentCommandResult<string>.FreshSuccess(response));
        Assert.NotNull(new IncidentCommandResult<string>.FreshPreconditionFailed("\"strong\""));
        Assert.NotNull(new IncidentCommandResult<string>.FreshStateConflict("state", "\"strong\""));
        Assert.NotNull(new IncidentCommandResult<string>.NotFound());
        Assert.NotNull(new IncidentCommandResult<string>.DependencyFailure());
        Assert.NotNull(new IncidentCommandResult<string>.IndeterminateCommit());
        Assert.NotNull(new LockedCommandDecision<string>.Apply(outcome));
        Assert.NotNull(new LockedCommandDecision<string>.StateConflict("state"));

        Assert.Throws<ArgumentException>(() => new IncidentCommandResponseContext(Guid.Empty, "\"strong\"", CorrelationId, completedAt));
        Assert.Throws<ArgumentException>(() => new IncidentCommandResponseContext(IncidentId, "\"strong\"", " ", completedAt));
        Assert.Throws<ArgumentException>(() => new IncidentCommandResponseContext(IncidentId, "\"strong\"", CorrelationId.ToUpperInvariant(), completedAt));
        Assert.Throws<ArgumentException>(() => new IncidentCommandResponseContext(IncidentId, "W/\"strong\"", CorrelationId, completedAt));
        Assert.Throws<ArgumentException>(() => new IncidentCommandResponseContext(IncidentId, "\"strong\"", CorrelationId, completedAt.ToOffset(TimeSpan.FromHours(7))));
        Assert.Throws<ArgumentException>(() => new StagedCommandOutcome(Guid.Empty, "acknowledgement", completedAt));
        Assert.Throws<ArgumentException>(() => new StagedCommandOutcome(Guid.NewGuid(), "invalid", completedAt));
        Assert.Throws<ArgumentException>(() => new StagedCommandOutcome(Guid.NewGuid(), "manual_resolution", completedAt.ToOffset(TimeSpan.FromHours(7))));
        Assert.Throws<ArgumentNullException>(() => new LockedCommandDecision<string>.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => new LockedCommandDecision<string>.StateConflict(null!));
        Assert.Throws<ArgumentNullException>(() => new IncidentCommandResult<string>.Replay(null!));
        Assert.Throws<ArgumentNullException>(() => new IncidentCommandResult<string>.FreshSuccess(null!));
        Assert.Throws<ArgumentException>(() => new IncidentCommandResult<string>.FreshPreconditionFailed("weak"));
        Assert.Throws<ArgumentNullException>(() => new IncidentCommandResult<string>.FreshStateConflict(null!, "\"strong\""));

        var immutableTypes = new[]
        {
            typeof(IncidentCommandResponseContext), typeof(StagedCommandOutcome),
            typeof(LockedCommandDecision<string>.Apply), typeof(LockedCommandDecision<string>.StateConflict),
            typeof(IncidentCommandResult<string>.Replay), typeof(IncidentCommandResult<string>.FreshSuccess),
            typeof(IncidentCommandResult<string>.FreshPreconditionFailed), typeof(IncidentCommandResult<string>.FreshStateConflict),
            typeof(IncidentCommandResult<string>.NotFound), typeof(IncidentCommandResult<string>.DependencyFailure),
            typeof(IncidentCommandResult<string>.IndeterminateCommit)
        };
        Assert.All(immutableTypes, type => Assert.DoesNotContain(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), property =>
            property.GetSetMethod(true) is not null));
    }

    [Fact]
    public void RuntimeFoundationTypesAreInternalAndAsyncInterfacesPutCancellationLast()
    {
        var types = new[]
        {
            typeof(IncidentCommandRoute), typeof(IncidentCommandInput<>), typeof(StoredIncidentHttpResponse),
            typeof(IncidentCommandResponseContext), typeof(StagedCommandOutcome), typeof(LockedCommandDecision<>),
            typeof(IncidentCommandResult<>), typeof(IIncidentLockedCommandHandler<,>), typeof(IIncidentCommandCoordinator),
            typeof(LockedIncidentExecutionContext<>), typeof(IIncidentEtagProvider), typeof(IncidentEtagV1),
            typeof(IncidentRequestFingerprintV1), typeof(IIncidentRequestFingerprintProvider), typeof(IncidentRequestFingerprintV1Provider),
            typeof(IncidentIdempotencyAdvisoryKeyDeriver), typeof(IIncidentIdempotencyAdvisoryKeyProvider),
            typeof(IncidentIdempotencyAdvisoryKeyProvider), typeof(IIncidentCommitBoundary), typeof(IncidentCommitBoundary),
            typeof(IIncidentTransactionBoundary), typeof(IncidentTransactionBoundary), typeof(IncidentReceiptReplayRecord),
            typeof(IncidentCommandCoordinator), typeof(IncidentRuntimeBoundaryValidation)
        };

        Assert.All(types, type => Assert.True(type.IsNotPublic));
        Assert.All(typeof(IIncidentLockedCommandHandler<,>).GetMethods().Concat(typeof(IIncidentCommandCoordinator).GetMethods()), method =>
        {
            Assert.Equal(typeof(CancellationToken), method.GetParameters()[^1].ParameterType);
        });
    }

    [Fact]
    public void LockedContextHasNoPersistenceOrTransactionCapability()
    {
        var members = typeof(LockedIncidentExecutionContext<string>).GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forbidden = new[] { "DbContext", "Transaction", "SaveChanges", "Commit", "Rollback", "Receipt", "Lock" };

        Assert.DoesNotContain(members, member => forbidden.Any(token => member.Name.Contains(token, StringComparison.Ordinal)));
    }

    [Fact]
    public void CommandResultsAreStructurallyDistinctAndRuntimeBoundariesRemainInternal()
    {
        var resultType = typeof(IncidentCommandResult<string>);
        var resultCases = resultType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Equal(8, resultCases.Length);
        Assert.Equal(IncidentCommandResultCaseNames, resultCases.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(resultCases, type => Assert.True(type.IsSealed));
        Assert.Contains(resultCases, type => type.Name == nameof(IncidentCommandResult<string>.DependencyFailure));
        Assert.Contains(typeof(IIncidentCommandCoordinator).Assembly.GetTypes(), type => type.Name == "IncidentCommandCoordinator");
        Assert.True(typeof(IncidentEndpoints).IsNotPublic);
        Assert.DoesNotContain(typeof(IncidentCommandResult<string>.Replay).GetConstructors(), constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType.Name.Contains("Handler", StringComparison.Ordinal) ||
                parameter.ParameterType.Name.Contains("Locked", StringComparison.Ordinal)));
        Assert.All(typeof(IncidentCommandResult<string>.DependencyFailure)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), constructor =>
            Assert.Empty(constructor.GetParameters()));
        Assert.Empty(typeof(IncidentCommandResult<string>.DependencyFailure)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        Assert.All(new[]
        {
            typeof(IncidentCommandResult<string>.NotFound),
            typeof(IncidentCommandResult<string>.DependencyFailure),
            typeof(IncidentCommandResult<string>.IndeterminateCommit)
        }, type =>
        {
            Assert.Empty(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            Assert.Empty(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            Assert.All(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), constructor =>
                Assert.Empty(constructor.GetParameters()));
        });
    }

    private static void AssertField(ImmutableArray<byte> value, ref int offset, ReadOnlySpan<byte> expected)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(offset, sizeof(uint))));
        offset += sizeof(uint);
        Assert.Equal(expected.ToArray(), value.AsSpan(offset, length).ToArray());
        offset += length;
    }

    private static byte[] LastField(ImmutableArray<byte> canonical)
    {
        var offset = IncidentRequestFingerprintV1.Marker.Length + 1;
        for (var field = 0; field < 6; field++)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(canonical.AsSpan(offset, sizeof(uint))));
            offset += sizeof(uint);
            if (field == 5) return canonical.AsSpan(offset, length).ToArray();
            offset += length;
        }

        throw new InvalidOperationException("The frozen fingerprint must contain six fields.");
    }

    private static byte[] FieldWithPrefix(ImmutableArray<byte> canonical, int fieldIndex)
    {
        var offset = IncidentRequestFingerprintV1.Marker.Length + 1;
        for (var field = 0; field < 6; field++)
        {
            var prefixOffset = offset;
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(canonical.AsSpan(offset, sizeof(uint))));
            offset += sizeof(uint);
            if (field == fieldIndex) return canonical.AsSpan(prefixOffset, sizeof(uint) + length).ToArray();
            offset += length;
        }

        throw new ArgumentOutOfRangeException(nameof(fieldIndex));
    }

    private static void AssertGetOnlyNonRecordState(Type type, params string[] expectedPropertyNames)
    {
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Equal(expectedPropertyNames.Order(), properties.Select(property => property.Name).Order());
        Assert.All(properties, property => Assert.Null(property.GetSetMethod(true)));
        Assert.DoesNotContain(properties, property => property.PropertyType.IsArray);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), method =>
            method.Name is "Deconstruct" or "<Clone>$");
    }
}
