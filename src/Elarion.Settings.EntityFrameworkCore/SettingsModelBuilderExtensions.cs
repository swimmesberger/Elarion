using Microsoft.EntityFrameworkCore;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>Registers the Elarion settings table on a <see cref="ModelBuilder"/>.</summary>
public static class SettingsModelBuilderExtensions {
    /// <summary>
    /// Maps the <see cref="Setting"/> entity to the <c>elarion_settings</c> table (by default) with
    /// snake_case columns and the composite key <c>(kind, owner, key)</c>. Call this from your context's
    /// <c>OnModelCreating</c> (alongside, for example, <c>UseElarionOutbox()</c>).
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="tableName">The table name. Defaults to <c>elarion_settings</c>.</param>
    /// <param name="schema">The schema, or <see langword="null"/> to use the provider's default schema.</param>
    public static ModelBuilder UseElarionSettings(
        this ModelBuilder modelBuilder,
        string tableName = "elarion_settings",
        string? schema = null) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        modelBuilder.Entity<Setting>(builder => {
            builder.ToTable(tableName, schema);
            builder.HasKey(setting => new { setting.Kind, setting.Owner, setting.Key });

            builder.Property(setting => setting.Kind).HasColumnName("kind").HasMaxLength(64);
            builder.Property(setting => setting.Owner).HasColumnName("owner").HasMaxLength(256);
            builder.Property(setting => setting.Key).HasColumnName("key").HasMaxLength(512);
            builder.Property(setting => setting.Value).HasColumnName("value");
            builder.Property(setting => setting.UpdatedOnUtc).HasColumnName("updated_on_utc");

            // Marked as a concurrency token for tracked saves; the store also guards writes explicitly so the
            // optimistic-concurrency contract holds under the change-tracker-free ExecuteUpdate/ExecuteDelete path.
            builder.Property(setting => setting.Version).HasColumnName("version").IsConcurrencyToken();
        });

        return modelBuilder;
    }
}
