using Microsoft.EntityFrameworkCore;

namespace Elarion.Messaging.Outbox;

/// <summary>
/// Configures the EF Core model used by the transactional outbox.
/// </summary>
public static class OutboxModelBuilderExtensions
{
    /// <summary>
    /// Adds the <see cref="OutboxMessage"/> table to the EF Core model. Call from <c>OnModelCreating</c> on the context
    /// that owns the business entities so the outbox row commits in the same transaction.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder UseElarionOutbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("elarion_outbox_messages");
            builder.HasKey(message => message.Id);

            // Partial index over pending rows only: the worker's "oldest undelivered first" scan stays a tiny indexed
            // probe regardless of how many delivered rows await retention purge. Filtered indexes are supported by
            // PostgreSQL, SQL Server, and SQLite; on MySQL, replace this with an unfiltered index in your own model.
            builder.HasIndex(message => message.OccurredOnUtc)
                .HasFilter("processed_on_utc IS NULL");

            builder.Property(message => message.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            builder.Property(message => message.OccurredOnUtc)
                .HasColumnName("occurred_on_utc");

            builder.Property(message => message.EventType)
                .HasColumnName("event_type")
                .IsRequired();

            builder.Property(message => message.Payload)
                .HasColumnName("payload")
                .IsRequired();

            builder.Property(message => message.CorrelationId)
                .HasColumnName("correlation_id");

            builder.Property(message => message.Attempts)
                .HasColumnName("attempts");

            builder.Property(message => message.ProcessedOnUtc)
                .HasColumnName("processed_on_utc");

            builder.Property(message => message.LockId)
                .HasColumnName("lock_id");

            builder.Property(message => message.LockedUntilUtc)
                .HasColumnName("locked_until_utc");

            builder.Property(message => message.Error)
                .HasColumnName("error");
        });

        return modelBuilder;
    }
}
