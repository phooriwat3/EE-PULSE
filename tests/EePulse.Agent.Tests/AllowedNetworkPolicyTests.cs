using System.Net;
using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Networking;
using EePulse.Agent.Core.Probing;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Tests;

public sealed class AllowedNetworkPolicyTests
{
    [Fact]
    public void PolicyNormalizesAndContainsOnlyUsableAddresses()
    {
        Assert.True(AllowedNetworkPolicy.TryCreate(["192.0.2.99/24"], false, out var policy));

        Assert.Equal(["192.0.2.0/24"], policy!.Networks);
        Assert.True(policy.Contains("192.0.2.10"));
        Assert.False(policy.Contains("192.0.2.0"));
        Assert.False(policy.Contains("192.0.2.255"));
        Assert.False(policy.Contains("198.51.100.1"));
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("0.0.0.0/8")]
    [InlineData("224.0.0.0/4")]
    [InlineData("127.0.0.0/8")]
    [InlineData("169.254.0.0/16")]
    [InlineData("2001:db8::/32")]
    [InlineData("255.255.255.254/31")]
    public void ProductionPolicyRejectsProhibitedScope(string value)
    {
        Assert.False(AllowedNetworkPolicy.TryCreate([value], false, out _));
    }

    [Fact]
    public void RedundantOverlapsAreRejected()
    {
        Assert.False(AllowedNetworkPolicy.TryCreate(["192.0.2.0/24", "192.0.2.128/25"], false, out _));
    }

    [Fact]
    public void NetworkCountBoundaryIsEnforced()
    {
        var maximum = Enumerable.Range(0, 64).Select(index => $"192.0.2.{index + 1}/32").ToArray();
        var overMaximum = maximum.Append("198.51.100.1/32");

        Assert.True(AllowedNetworkPolicy.TryCreate(maximum, false, out _));
        Assert.False(AllowedNetworkPolicy.TryCreate(overMaximum, false, out _));
    }

    [Fact]
    public void InvalidSchemaThresholdAndDuplicateProbeAreRejected()
    {
        Assert.True(AllowedNetworkPolicy.TryCreate(["192.0.2.0/24"], false, out var local));
        var validator = new AgentConfigurationValidator(false);
        var valid = Configuration();

        Assert.Equal(ConfigurationRejectionCode.SchemaUnsupported,
            validator.Validate(valid with { SchemaVersion = 2 }, valid.AgentId, valid.AgentGroupId, 0, local!));
        Assert.Equal(ConfigurationRejectionCode.ProbeInvalid,
            validator.Validate(valid with
            {
                Probes = [valid.Probes[0] with { FailureThreshold = 0 }],
            }, valid.AgentId, valid.AgentGroupId, 0, local!));
        Assert.Equal(ConfigurationRejectionCode.DuplicateProbe,
            validator.Validate(valid with
            {
                Probes = [valid.Probes[0], valid.Probes[0]],
            }, valid.AgentId, valid.AgentGroupId, 0, local!));
    }

    [Fact]
    public void PointToPointAndHostPrefixesPermitTheirEndpoints()
    {
        Assert.True(AllowedNetworkPolicy.TryCreate(["192.0.2.10/31", "198.51.100.20/32"], false, out var policy));

        Assert.True(policy!.Contains("192.0.2.10"));
        Assert.True(policy.Contains("192.0.2.11"));
        Assert.True(policy.Contains("198.51.100.20"));
    }

    [Fact]
    public async Task DirectedBroadcastNeverReachesTransport()
    {
        Assert.True(AllowedNetworkPolicy.TryCreate(["192.0.2.0/24"], false, out var policy));
        var inner = new CountingTransport();
        var transport = new AllowedNetworkProbeTransport(inner, () => policy, () => policy);

        await Assert.ThrowsAsync<NetworkPolicyViolationException>(async () =>
            await transport.SendAsync(
                new ProbeTransportRequest("192.0.2.255", TimeSpan.FromSeconds(1)),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public void DirectedBroadcastRejectsCompleteConfiguration()
    {
        Assert.True(AllowedNetworkPolicy.TryCreate(["192.0.2.0/24"], false, out var local));
        var configuration = Configuration(target: "192.0.2.255");

        var rejection = new AgentConfigurationValidator(false).Validate(
            configuration,
            configuration.AgentId,
            configuration.AgentGroupId,
            activeVersion: 0,
            local!);

        Assert.Equal(ConfigurationRejectionCode.ProbeInvalid, rejection);
    }

    [Fact]
    public void RemotePolicyCannotExpandLocalCeiling()
    {
        Assert.True(AllowedNetworkPolicy.TryCreate(["192.0.2.0/25"], false, out var local));
        var configuration = Configuration(allowedNetworks: ["192.0.2.0/24"]);

        var rejection = new AgentConfigurationValidator(false).Validate(
            configuration,
            configuration.AgentId,
            configuration.AgentGroupId,
            activeVersion: 0,
            local!);

        Assert.Equal(ConfigurationRejectionCode.NetworkPolicyMismatch, rejection);
    }

    [Fact]
    public void HigherVersionRollbackContentIsAcceptedButVersionRegressionIsRejected()
    {
        Assert.True(AllowedNetworkPolicy.TryCreate(["192.0.2.0/24"], false, out var local));
        var validator = new AgentConfigurationValidator(false);
        var rollback = Configuration(version: 6) with { RollbackOfVersion = 2 };

        Assert.Null(validator.Validate(rollback, rollback.AgentId, rollback.AgentGroupId, 5, local!));
        Assert.Equal(
            ConfigurationRejectionCode.VersionNotMonotonic,
            validator.Validate(rollback with { ConfigurationVersion = 4 }, rollback.AgentId, rollback.AgentGroupId, 5, local!));
    }

    internal static AgentConfigurationResponse Configuration(
        string target = "192.0.2.10",
        long version = 1,
        IReadOnlyList<string>? allowedNetworks = null,
        Guid? agentId = null,
        Guid? groupId = null) =>
        new(
            AgentContract.SchemaVersion,
            agentId ?? Guid.NewGuid(),
            groupId ?? Guid.NewGuid(),
            version,
            DateTimeOffset.UtcNow,
            null,
            allowedNetworks ?? ["192.0.2.0/24"],
            [new AgentProbeConfiguration(
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                "icmp",
                target,
                30,
                2_000,
                3,
                100,
                200,
                3,
                2)]);

    private sealed class CountingTransport : IProbeTransport
    {
        public int CallCount { get; private set; }

        public ValueTask<ProbeTransportReply> SendAsync(
            ProbeTransportRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new ProbeTransportReply(ProbeTransportStatus.Succeeded, TimeSpan.Zero));
        }
    }
}
