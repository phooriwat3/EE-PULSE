using EePulse.Agent.Core.Configuration;
using EePulse.Agent.Core.Identity;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Tests;

public sealed class ConfigurationApplicationTests
{
    [Fact]
    public async Task ApplyAtomicallyAdvancesAndRetainsPriorConfiguration()
    {
        var identity = Identity();
        var first = AllowedNetworkPolicyTests.Configuration(agentId: identity.AgentId, groupId: identity.AgentGroupId);
        var second = first with { ConfigurationVersion = 2, GeneratedAt = first.GeneratedAt.AddMinutes(1) };
        var store = new MemoryConfigurationStore();
        var sink = new RecordingScheduleSink();
        var applier = new AgentConfigurationApplier(new AgentConfigurationValidator(false), store, sink);

        Assert.True((await applier.ApplyAsync(identity, first, "\"v1\"", false, TestContext.Current.CancellationToken)).Applied);
        Assert.True((await applier.ApplyAsync(identity, second, "\"v2\"", false, TestContext.Current.CancellationToken)).Applied);

        Assert.Equal(2, store.Value!.Active.ConfigurationVersion);
        Assert.Equal(1, store.Value.LastKnownGood!.ConfigurationVersion);
        Assert.Equal(2, sink.Active!.ConfigurationVersion);
    }

    [Fact]
    public async Task SchedulerFailureRestoresLastKnownGood()
    {
        var identity = Identity();
        var first = AllowedNetworkPolicyTests.Configuration(agentId: identity.AgentId, groupId: identity.AgentGroupId);
        var second = first with { ConfigurationVersion = 2 };
        var store = new MemoryConfigurationStore
        {
            Value = new StoredAgentConfiguration(first, null, "\"v1\""),
        };
        var sink = new RecordingScheduleSink { FailVersion = 2, Active = first };
        var applier = new AgentConfigurationApplier(new AgentConfigurationValidator(false), store, sink);

        var result = await applier.ApplyAsync(identity, second, "\"v2\"", false, TestContext.Current.CancellationToken);

        Assert.False(result.Applied);
        Assert.Equal(ConfigurationRejectionCode.SchedulerApplyFailed, result.RejectionCode);
        Assert.Equal(1, store.Value!.Active.ConfigurationVersion);
        Assert.Equal(1, sink.Active!.ConfigurationVersion);
    }

    [Fact]
    public async Task StorageFailureLeavesLastKnownGoodActive()
    {
        var identity = Identity();
        var first = AllowedNetworkPolicyTests.Configuration(agentId: identity.AgentId, groupId: identity.AgentGroupId);
        var store = new FailingConfigurationStore(new StoredAgentConfiguration(first, null, "\"v1\""));
        var sink = new RecordingScheduleSink { Active = first };
        using var applier = new AgentConfigurationApplier(new AgentConfigurationValidator(false), store, sink);

        var result = await applier.ApplyAsync(
            identity,
            first with { ConfigurationVersion = 2 },
            "\"v2\"",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationRejectionCode.ConfigurationStorageFailed, result.RejectionCode);
        Assert.Equal(1, sink.Active!.ConfigurationVersion);
        Assert.Equal(1, (await store.LoadAsync(TestContext.Current.CancellationToken))!.Active.ConfigurationVersion);
    }

    [Fact]
    public async Task CancellationDuringSchedulerSwapRestoresPriorDurableAndActiveState()
    {
        var identity = Identity();
        var first = AllowedNetworkPolicyTests.Configuration(agentId: identity.AgentId, groupId: identity.AgentGroupId);
        var second = first with { ConfigurationVersion = 2 };
        var store = new MemoryConfigurationStore
        {
            Value = new StoredAgentConfiguration(first, null, "\"v1\""),
        };
        using var cancellation = new CancellationTokenSource();
        var sink = new CancellingScheduleSink(cancellation, first);
        using var applier = new AgentConfigurationApplier(new AgentConfigurationValidator(false), store, sink);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await applier.ApplyAsync(identity, second, "\"v2\"", false, cancellation.Token));

        Assert.Equal(1, store.Value!.Active.ConfigurationVersion);
        Assert.Equal(1, sink.Active!.ConfigurationVersion);
    }

    [Fact]
    public async Task CancellationDuringFirstSchedulerSwapClearsDurableCandidate()
    {
        var identity = Identity();
        var first = AllowedNetworkPolicyTests.Configuration(agentId: identity.AgentId, groupId: identity.AgentGroupId);
        var store = new MemoryConfigurationStore();
        using var cancellation = new CancellationTokenSource();
        var sink = new CancellingScheduleSink(cancellation, null, cancelVersion: 1);
        using var applier = new AgentConfigurationApplier(new AgentConfigurationValidator(false), store, sink);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await applier.ApplyAsync(identity, first, "\"v1\"", false, cancellation.Token));

        Assert.Null(store.Value);
        Assert.Null(sink.Active);
        Assert.True(sink.Halted);
    }

    internal static AgentIdentity Identity(AgentCredential? pending = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "agent-test",
            "1.0.0",
            ["192.0.2.0/24"],
            new AgentCredential(Guid.NewGuid(), RuntimeSecret(), DateTimeOffset.UtcNow.AddDays(90), DateTimeOffset.UtcNow.AddDays(75)),
            pending,
            20,
            60,
            1);

    internal static string RuntimeSecret() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    internal sealed class MemoryConfigurationStore : IAgentConfigurationStore
    {
        public StoredAgentConfiguration? Value { get; set; }

        public ValueTask<StoredAgentConfiguration?> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Value);

        public ValueTask SaveAsync(StoredAgentConfiguration configuration, CancellationToken cancellationToken)
        {
            Value = configuration;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken)
        {
            Value = null;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class RecordingScheduleSink : IAgentScheduleSink
    {
        public long? FailVersion { get; init; }
        public AgentConfigurationResponse? Active { get; set; }
        public bool Halted { get; private set; }

        public ValueTask ReplaceAsync(AgentConfigurationResponse configuration, CancellationToken cancellationToken)
        {
            if (configuration.ConfigurationVersion == FailVersion)
            {
                throw new IOException("Synthetic atomic apply failure.");
            }

            Active = configuration;
            Halted = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask HaltAsync(CancellationToken cancellationToken)
        {
            Active = null;
            Halted = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingConfigurationStore(StoredAgentConfiguration value) : IAgentConfigurationStore
    {
        public ValueTask<StoredAgentConfiguration?> LoadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<StoredAgentConfiguration?>(value);

        public ValueTask SaveAsync(StoredAgentConfiguration configuration, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("Synthetic storage failure."));

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CancellingScheduleSink(
        CancellationTokenSource cancellation,
        AgentConfigurationResponse? active,
        long cancelVersion = 2) : IAgentScheduleSink
    {
        public AgentConfigurationResponse? Active { get; private set; } = active;
        public bool Halted { get; private set; }

        public ValueTask ReplaceAsync(AgentConfigurationResponse configuration, CancellationToken cancellationToken)
        {
            if (configuration.ConfigurationVersion == cancelVersion)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }

            Active = configuration;
            Halted = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask HaltAsync(CancellationToken cancellationToken)
        {
            Active = null;
            Halted = true;
            return ValueTask.CompletedTask;
        }
    }
}
