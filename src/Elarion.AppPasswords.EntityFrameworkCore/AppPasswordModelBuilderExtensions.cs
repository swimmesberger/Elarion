using Microsoft.EntityFrameworkCore;

namespace Elarion.AppPasswords.EntityFrameworkCore;

/// <summary>
/// Maps the <see cref="AppPasswordEntity"/> onto a model. Normally applied through the
/// <see cref="GenerateElarionAppPasswordsAttribute"/> seam; call it directly from <c>OnModelCreating</c> when the
/// context is hand-written.
/// </summary>
public static class AppPasswordModelBuilderExtensions {
    /// <summary>The longest stored hash: 64 characters (a SHA-256 in Base64 takes 44).</summary>
    public const int MaxHashLength = 64;

    /// <summary>
    /// Adds the app-password table: <c>elarion_app_passwords</c> with the columns <c>id</c>, <c>user_id</c>,
    /// <c>name</c>, <c>scope</c>, <c>hash</c> (unique), <c>created_at</c> and <c>last_used_at</c> in snake_case.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="tableName">Overrides the table name; defaults per <paramref name="snakeCase"/>.</param>
    /// <param name="schema">Optional schema.</param>
    /// <param name="snakeCase">Whether table and column names default to snake_case (the Elarion default).</param>
    public static ModelBuilder UseElarionAppPasswords(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var table = tableName ?? (snakeCase ? "elarion_app_passwords" : "ElarionAppPasswords");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<AppPasswordEntity>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id)
                .HasColumnName(snakeCase ? "id" : "Id")
                .ValueGeneratedNever();
            builder.Property(entity => entity.UserId)
                .HasColumnName(snakeCase ? "user_id" : "UserId");
            builder.Property(entity => entity.Name)
                .HasColumnName(snakeCase ? "name" : "Name")
                .HasMaxLength(AppPasswordOptions.MaxNameLength);
            builder.Property(entity => entity.Scope)
                .HasColumnName(snakeCase ? "scope" : "Scope")
                .HasMaxLength(AppPasswordScope.MaxLength);
            builder.Property(entity => entity.Hash)
                .HasColumnName(snakeCase ? "hash" : "Hash")
                .HasMaxLength(MaxHashLength);
            builder.Property(entity => entity.CreatedAt)
                .HasColumnName(snakeCase ? "created_at" : "CreatedAt");
            builder.Property(entity => entity.LastUsedAt)
                .HasColumnName(snakeCase ? "last_used_at" : "LastUsedAt");
            // Every authenticated request looks its credential up by hash; unique, so two secrets cannot collide.
            builder.HasIndex(entity => entity.Hash).IsUnique();
            // Listing, revoking and the per-user cap filter by user.
            builder.HasIndex(entity => entity.UserId);
        });
        return modelBuilder;
    }
}
