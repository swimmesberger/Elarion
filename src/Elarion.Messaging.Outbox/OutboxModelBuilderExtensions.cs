using Microsoft.EntityFrameworkCore;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Configures the EF Core model used by the transactional outbox.
/// </summary>
public static class OutboxModelBuilderExtensions
{
    /// <summary>
    /// Adds the <see cref="OutboxMessage"/> table to the EF Core model. Call from <c>OnModelCreating</c> on the context
    /// that owns the business entities so the outbox row commits in the same transaction — or annotate the context with
    /// <c>[GenerateElarionOutbox]</c> and let the bundled generator call this for you.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="tableName">
    /// The table name, or <see langword="null"/> for the default (<c>elarion_outbox_messages</c> /
    /// <c>ElarionOutboxMessages</c> depending on <paramref name="snakeCase"/>).
    /// </param>
    /// <param name="schema">The schema, or <see langword="null"/> to use the provider's default schema.</param>
    /// <param name="snakeCase">Whether to use snake_case table/column names. Defaults to <see langword="true"/>.</param>
    /// <param name="deliveryTableName">The per-consumer delivery table name, or <see langword="null"/> for the default.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder UseElarionOutbox(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true,
        string? deliveryTableName = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var table = tableName ?? (snakeCase ? "elarion_outbox_messages" : "ElarionOutboxMessages");
        var deliveryTable = deliveryTableName
            ?? (snakeCase ? "elarion_outbox_deliveries" : "ElarionOutboxDeliveries");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryTable);

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable(table, schema);
            builder.HasKey(message => message.Id);

            builder.Property(message => message.Id)
                .HasColumnName(snakeCase ? "id" : "Id")
                .ValueGeneratedNever();

            builder.Property(message => message.OccurredOnUtc)
                .HasColumnName(snakeCase ? "occurred_on_utc" : "OccurredOnUtc");

            builder.Property(message => message.EventType)
                .HasColumnName(snakeCase ? "event_type" : "EventType")
                .IsRequired();

            builder.Property(message => message.Payload)
                .HasColumnName(snakeCase ? "payload" : "Payload")
                .IsRequired();

            builder.Property(message => message.CorrelationId)
                .HasColumnName(snakeCase ? "correlation_id" : "CorrelationId");

            builder.Property(message => message.TraceParent)
                .HasColumnName(snakeCase ? "trace_parent" : "TraceParent")
                .HasMaxLength(55);

        });

        modelBuilder.Entity<OutboxDelivery>(builder => {
            builder.ToTable(deliveryTable, schema);
            builder.HasKey(delivery => delivery.Id);
            builder.HasIndex(delivery => new { delivery.MessageId, delivery.ConsumerId }).IsUnique();
            builder.HasIndex(delivery => delivery.ProcessedOnUtc)
                .HasDatabaseName(snakeCase ? $"ix_{deliveryTable}_purge" : $"IX_{deliveryTable}_Purge")
                .HasFilter(snakeCase ? "processed_on_utc IS NOT NULL" : "\"ProcessedOnUtc\" IS NOT NULL");
            builder.HasIndex(delivery => new { delivery.TargetRole, delivery.OccurredOnUtc, delivery.Id })
                .HasDatabaseName(snakeCase ? $"ix_{deliveryTable}_claim" : $"IX_{deliveryTable}_Claim")
                .HasFilter(snakeCase ? "processed_on_utc IS NULL" : "\"ProcessedOnUtc\" IS NULL");

            builder.Property(delivery => delivery.Id)
                .HasColumnName(snakeCase ? "id" : "Id")
                .ValueGeneratedNever();
            builder.Property(delivery => delivery.MessageId)
                .HasColumnName(snakeCase ? "message_id" : "MessageId");
            builder.Property(delivery => delivery.OccurredOnUtc)
                .HasColumnName(snakeCase ? "occurred_on_utc" : "OccurredOnUtc");
            builder.Property(delivery => delivery.ConsumerId)
                .HasColumnName(snakeCase ? "consumer_id" : "ConsumerId")
                .HasMaxLength(512)
                .IsRequired();
            builder.Property(delivery => delivery.TargetRole)
                .HasColumnName(snakeCase ? "target_role" : "TargetRole")
                .HasMaxLength(200);
            builder.Property(delivery => delivery.Attempts)
                .HasColumnName(snakeCase ? "attempts" : "Attempts");
            builder.Property(delivery => delivery.ProcessedOnUtc)
                .HasColumnName(snakeCase ? "processed_on_utc" : "ProcessedOnUtc");
            builder.Property(delivery => delivery.LockId)
                .HasColumnName(snakeCase ? "lock_id" : "LockId");
            builder.Property(delivery => delivery.LockedUntilUtc)
                .HasColumnName(snakeCase ? "locked_until_utc" : "LockedUntilUtc");
            builder.Property(delivery => delivery.Error)
                .HasColumnName(snakeCase ? "error" : "Error");

            builder.HasOne(delivery => delivery.Message)
                .WithMany(message => message.Deliveries)
                .HasForeignKey(delivery => delivery.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        return modelBuilder;
    }
}
