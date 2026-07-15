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
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder UseElarionOutbox(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var table = tableName ?? (snakeCase ? "elarion_outbox_messages" : "ElarionOutboxMessages");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable(table, schema);
            builder.HasKey(message => message.Id);
            builder.HasIndex(message => message.ProcessedOnUtc)
                .HasDatabaseName(snakeCase ? $"ix_{table}_purge" : $"IX_{table}_Purge")
                .HasFilter(snakeCase ? "processed_on_utc IS NOT NULL" : "\"ProcessedOnUtc\" IS NOT NULL");
            builder.HasIndex(message => new { message.TargetRole, message.OccurredOnUtc, message.Id })
                .HasDatabaseName(snakeCase ? $"ix_{table}_claim" : $"IX_{table}_Claim")
                .HasFilter(snakeCase ? "processed_on_utc IS NULL" : "\"ProcessedOnUtc\" IS NULL");

            builder.Property(message => message.Id)
                .HasColumnName(snakeCase ? "id" : "Id")
                .ValueGeneratedNever();

            builder.Property(message => message.MessageId)
                .HasColumnName(snakeCase ? "message_id" : "MessageId");

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

            builder.Property(message => message.ConsumerIdsJson)
                .HasColumnName(snakeCase ? "consumer_ids" : "ConsumerIds");

            builder.Property(message => message.TargetRole)
                .HasColumnName(snakeCase ? "target_role" : "TargetRole")
                .HasMaxLength(200);

            builder.Property(message => message.Attempts)
                .HasColumnName(snakeCase ? "attempts" : "Attempts");
            builder.Property(message => message.ProcessedOnUtc)
                .HasColumnName(snakeCase ? "processed_on_utc" : "ProcessedOnUtc");
            builder.Property(message => message.LockId)
                .HasColumnName(snakeCase ? "lock_id" : "LockId");
            builder.Property(message => message.LockedUntilUtc)
                .HasColumnName(snakeCase ? "locked_until_utc" : "LockedUntilUtc");
            builder.Property(message => message.Error)
                .HasColumnName(snakeCase ? "error" : "Error");
        });

        return modelBuilder;
    }
}
