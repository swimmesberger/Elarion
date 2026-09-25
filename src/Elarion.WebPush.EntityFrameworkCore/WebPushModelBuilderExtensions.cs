using Microsoft.EntityFrameworkCore;

namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// Maps <see cref="PushSubscriptionEntity"/> and <see cref="VapidKeyEntity"/> onto a model. Normally applied
/// through the <see cref="GenerateElarionWebPushAttribute"/> seam; call it directly from
/// <c>OnModelCreating</c> when the context is hand-written.
/// </summary>
public static class WebPushModelBuilderExtensions {
    /// <summary>Adds the push subscription and VAPID key tables to the model.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="subscriptionTableName">Overrides the subscription table name; defaults per <paramref name="snakeCase"/>.</param>
    /// <param name="vapidKeyTableName">Overrides the VAPID key table name; defaults per <paramref name="snakeCase"/>.</param>
    /// <param name="schema">Optional schema.</param>
    /// <param name="snakeCase">Whether table/column names default to snake_case (the Elarion default).</param>
    public static ModelBuilder UseElarionWebPush(
        this ModelBuilder modelBuilder,
        string? subscriptionTableName = null,
        string? vapidKeyTableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var subscriptionTable = subscriptionTableName ??
                                (snakeCase ? "elarion_push_subscriptions" : "ElarionPushSubscriptions");
        var keyTable = vapidKeyTableName ?? (snakeCase ? "elarion_vapid_keys" : "ElarionVapidKeys");
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyTable);

        modelBuilder.Entity<PushSubscriptionEntity>(builder => {
            builder.ToTable(subscriptionTable, schema);
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id)
                .HasColumnName(snakeCase ? "id" : "Id")
                .ValueGeneratedNever();
            builder.Property(entity => entity.Endpoint)
                .HasColumnName(snakeCase ? "endpoint" : "Endpoint")
                .HasMaxLength(WebPushSubscriptionService.MaxEndpointLength);
            builder.Property(entity => entity.P256dh)
                .HasColumnName(snakeCase ? "p256dh" : "P256dh")
                .HasMaxLength(128);
            builder.Property(entity => entity.Auth)
                .HasColumnName(snakeCase ? "auth" : "Auth")
                .HasMaxLength(64);
            builder.Property(entity => entity.UserId)
                .HasColumnName(snakeCase ? "user_id" : "UserId")
                .HasMaxLength(256);
            builder.Property(entity => entity.UserAgent)
                .HasColumnName(snakeCase ? "user_agent" : "UserAgent")
                .HasMaxLength(WebPushSubscriptionService.MaxUserAgentLength);
            builder.Property(entity => entity.CreatedOnUtc)
                .HasColumnName(snakeCase ? "created_on_utc" : "CreatedOnUtc");
            builder.Property(entity => entity.LastSeenOnUtc)
                .HasColumnName(snakeCase ? "last_seen_on_utc" : "LastSeenOnUtc");
            // The upsert's conflict target: one row per endpoint, reassigned rather than duplicated.
            builder.HasIndex(entity => entity.Endpoint).IsUnique();
            // The fan-out's lookup.
            builder.HasIndex(entity => entity.UserId);
        });

        modelBuilder.Entity<VapidKeyEntity>(builder => {
            builder.ToTable(keyTable, schema);
            builder.HasKey(entity => entity.Name);
            builder.Property(entity => entity.Name)
                .HasColumnName(snakeCase ? "name" : "Name")
                .HasMaxLength(64);
            builder.Property(entity => entity.PublicKey)
                .HasColumnName(snakeCase ? "public_key" : "PublicKey")
                .HasMaxLength(128);
            builder.Property(entity => entity.PrivateKey)
                .HasColumnName(snakeCase ? "private_key" : "PrivateKey")
                .HasMaxLength(64);
            builder.Property(entity => entity.CreatedOnUtc)
                .HasColumnName(snakeCase ? "created_on_utc" : "CreatedOnUtc");
        });
        return modelBuilder;
    }
}
