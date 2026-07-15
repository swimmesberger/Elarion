using AwesomeAssertions;
using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace Elarion.Tests.Messaging;

public sealed class OutboxModelBuilderExtensionsTests {
    [Fact]
    public void UseElarionOutbox_Defaults_MapsElarionOutboxMessagesTable() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionOutbox();
        var model = modelBuilder.FinalizeModel();

        var outboxMessage = model.FindEntityType(typeof(OutboxMessage));
        outboxMessage.Should().NotBeNull();
        outboxMessage!.GetTableName().Should().Be("elarion_outbox_messages");
        outboxMessage.GetSchema().Should().BeNull();
        outboxMessage.FindProperty(nameof(OutboxMessage.TraceParent))!.GetColumnName().Should().Be("trace_parent");
    }

    [Fact]
    public void UseElarionOutbox_AddsPartialPurgeIndexOverProcessedRows() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionOutbox();
        var model = modelBuilder.FinalizeModel();

        // The retention purge's `processed_on_utc < cutoff` delete must stay an indexed probe; the pending
        // partial index cannot serve it.
        var delivery = model.FindEntityType(typeof(OutboxDelivery))!;
        delivery.GetTableName().Should().Be("elarion_outbox_deliveries");
        var purgeIndex = delivery.GetIndexes()
            .Single(index => index.Properties.Count == 1
                && index.Properties[0].Name == nameof(OutboxDelivery.ProcessedOnUtc));
        purgeIndex.GetDatabaseName().Should().Be("ix_elarion_outbox_deliveries_purge");
        purgeIndex.GetFilter().Should().Be("processed_on_utc IS NOT NULL");
    }

    [Fact]
    public void UseElarionOutbox_AddsOrderedPartialClaimIndex() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionOutbox();
        var model = modelBuilder.FinalizeModel();

        var delivery = model.FindEntityType(typeof(OutboxDelivery))!;
        delivery.FindProperty(nameof(OutboxDelivery.OccurredOnUtc))!.GetColumnName()
            .Should().Be("occurred_on_utc");
        var claimIndex = delivery.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(OutboxDelivery.TargetRole),
                nameof(OutboxDelivery.OccurredOnUtc),
                nameof(OutboxDelivery.Id)
            ]));
        claimIndex.GetDatabaseName().Should().Be("ix_elarion_outbox_deliveries_claim");
        claimIndex.GetFilter().Should().Be("processed_on_utc IS NULL");
    }

    [Fact]
    public void UseElarionOutbox_CustomTableAndSchema_AreApplied() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionOutbox("app_outbox", "messaging");
        var model = modelBuilder.FinalizeModel();

        var outboxMessage = model.FindEntityType(typeof(OutboxMessage));
        outboxMessage.Should().NotBeNull();
        outboxMessage!.GetTableName().Should().Be("app_outbox");
        outboxMessage.GetSchema().Should().Be("messaging");
        model.FindEntityType(typeof(OutboxDelivery))!.GetSchema().Should().Be("messaging");
    }

    [Fact]
    public void UseElarionOutbox_SnakeCaseFalse_UsesPascalCaseNames() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionOutbox(snakeCase: false);
        var model = modelBuilder.FinalizeModel();

        var outboxMessage = model.FindEntityType(typeof(OutboxMessage))!;
        outboxMessage.GetTableName().Should().Be("ElarionOutboxMessages");
        outboxMessage.FindProperty(nameof(OutboxMessage.OccurredOnUtc))!.GetColumnName().Should().Be("OccurredOnUtc");
        outboxMessage.FindProperty(nameof(OutboxMessage.TraceParent))!.GetColumnName().Should().Be("TraceParent");

        var delivery = model.FindEntityType(typeof(OutboxDelivery))!;
        delivery.GetTableName().Should().Be("ElarionOutboxDeliveries");
        delivery.FindProperty(nameof(OutboxDelivery.OccurredOnUtc))!.GetColumnName().Should().Be("OccurredOnUtc");
        delivery.FindProperty(nameof(OutboxDelivery.ProcessedOnUtc))!.GetColumnName().Should().Be("ProcessedOnUtc");
        var purgeIndex = delivery.GetIndexes()
            .Single(index => index.Properties.Count == 1
                && index.Properties[0].Name == nameof(OutboxDelivery.ProcessedOnUtc));
        purgeIndex.GetDatabaseName().Should().Be("IX_ElarionOutboxDeliveries_Purge");
        purgeIndex.GetFilter().Should().Be("\"ProcessedOnUtc\" IS NOT NULL");
    }
}
