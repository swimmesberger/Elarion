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
    }

    [Fact]
    public void UseElarionOutbox_SnakeCaseFalse_UsesPascalCaseNames() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionOutbox(snakeCase: false);
        var model = modelBuilder.FinalizeModel();

        var outboxMessage = model.FindEntityType(typeof(OutboxMessage))!;
        outboxMessage.GetTableName().Should().Be("ElarionOutboxMessages");
        outboxMessage.FindProperty(nameof(OutboxMessage.OccurredOnUtc))!.GetColumnName().Should().Be("OccurredOnUtc");
        outboxMessage.FindProperty(nameof(OutboxMessage.ProcessedOnUtc))!.GetColumnName().Should().Be("ProcessedOnUtc");

        // The pending partial index must reference the PascalCase column, quoted for PostgreSQL folding.
        outboxMessage.GetIndexes()
            .Single(index => index.Properties.Single().Name == nameof(OutboxMessage.OccurredOnUtc))
            .GetFilter().Should().Be("\"ProcessedOnUtc\" IS NULL");
    }
}
