using Microsoft.EntityFrameworkCore;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>Registers the Elarion settings table on a <see cref="ModelBuilder"/>.</summary>
public static class SettingsModelBuilderExtensions {
    /// <summary>
    /// Maps the <see cref="Setting"/> entity to the <c>elarion_settings</c> table (by default) with the composite
    /// key <c>(kind, owner, key)</c>. Call this from your context's <c>OnModelCreating</c> (alongside, for example,
    /// <c>UseElarionOutbox()</c>) — or annotate the context with <c>[GenerateElarionSettings]</c> and let the
    /// bundled generator call this for you.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="tableName">
    /// The table name, or <see langword="null"/> for the default (<c>elarion_settings</c> /
    /// <c>ElarionSettings</c> depending on <paramref name="snakeCase"/>).
    /// </param>
    /// <param name="schema">The schema, or <see langword="null"/> to use the provider's default schema.</param>
    /// <param name="snakeCase">Whether to use snake_case table/column names. Defaults to <see langword="true"/>.</param>
    public static ModelBuilder UseElarionSettings(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var table = tableName ?? (snakeCase ? "elarion_settings" : "ElarionSettings");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<Setting>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(setting => new { setting.Kind, setting.Owner, setting.Key });

            builder.Property(setting => setting.Kind).HasColumnName(snakeCase ? "kind" : "Kind").HasMaxLength(64);
            builder.Property(setting => setting.Owner).HasColumnName(snakeCase ? "owner" : "Owner").HasMaxLength(256);
            builder.Property(setting => setting.Key).HasColumnName(snakeCase ? "key" : "Key").HasMaxLength(512);
            builder.Property(setting => setting.Value).HasColumnName(snakeCase ? "value" : "Value");
            builder.Property(setting => setting.UpdatedOnUtc).HasColumnName(snakeCase ? "updated_on_utc" : "UpdatedOnUtc");

            // Marked as a concurrency token for tracked saves; the store also guards writes explicitly so the
            // optimistic-concurrency contract holds under the change-tracker-free ExecuteUpdate/ExecuteDelete path.
            builder.Property(setting => setting.Version).HasColumnName(snakeCase ? "version" : "Version").IsConcurrencyToken();
        });

        return modelBuilder;
    }
}
