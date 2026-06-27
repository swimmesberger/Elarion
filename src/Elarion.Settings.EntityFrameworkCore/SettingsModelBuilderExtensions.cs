using Microsoft.EntityFrameworkCore;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>Registers the Elarion settings table on a <see cref="ModelBuilder"/>.</summary>
public static class SettingsModelBuilderExtensions {
    /// <summary>
    /// Maps the <see cref="Setting"/> entity to the <c>elarion_settings</c> table with snake_case columns and
    /// the composite key <c>(kind, owner, key)</c>. Call this from your context's <c>OnModelCreating</c>
    /// (alongside, for example, <c>UseElarionOutbox()</c>).
    /// </summary>
    public static ModelBuilder UseElarionSettings(this ModelBuilder modelBuilder) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Setting>(builder => {
            builder.ToTable("elarion_settings");
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
