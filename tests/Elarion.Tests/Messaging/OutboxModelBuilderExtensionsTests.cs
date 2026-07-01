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
}
