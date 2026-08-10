using EePulse.Contracts;
using EePulse.Contracts.Agents;

namespace EePulse.Agent.Tests;

public sealed class ContractTests
{
    [Fact]
    public void EmptyResultBatchRetainsVersionAndIdentity()
    {
        var agentId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var batch = new ProbeResultBatchRequest(
            ApiVersions.Current,
            agentId,
            batchId,
            DateTimeOffset.UtcNow,
            1,
            []);

        Assert.Equal(ApiVersions.V1, batch.SchemaVersion);
        Assert.Equal(agentId, batch.AgentId);
        Assert.Equal(batchId, batch.BatchId);
        Assert.Empty(batch.Results);
    }
}
