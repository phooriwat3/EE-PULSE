using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EePulse.UnitTests;

public sealed class Wp07IncidentRuntimeTests
{
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
}
