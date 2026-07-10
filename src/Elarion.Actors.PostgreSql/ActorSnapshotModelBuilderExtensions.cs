using Microsoft.EntityFrameworkCore;

namespace Elarion.Actors.PostgreSql;

/// <summary>
/// Maps the <see cref="ActorSnapshotEntity"/> onto a model. Normally applied through the
/// <see cref="GenerateElarionActorSnapshotsAttribute"/> seam; call it directly from
/// <c>OnModelCreating</c> when the context is hand-written.
/// </summary>
public static class ActorSnapshotModelBuilderExtensions {
    /// <summary>Adds the actor snapshot table to the model.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="tableName">Overrides the table name; defaults per <paramref name="snakeCase"/>.</param>
    /// <param name="schema">Optional schema.</param>
    /// <param name="snakeCase">Whether table/column names default to snake_case (the Elarion default).</param>
    public static ModelBuilder UseElarionActorSnapshots(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var table = tableName ?? (snakeCase ? "elarion_actor_snapshots" : "ElarionActorSnapshots");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<ActorSnapshotEntity>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(entity => new { entity.ActorName, entity.ActorKey });
            builder.Property(entity => entity.ActorName)
                .HasColumnName(snakeCase ? "actor_name" : "ActorName")
                .HasMaxLength(256);
            builder.Property(entity => entity.ActorKey)
                .HasColumnName(snakeCase ? "actor_key" : "ActorKey")
                .HasMaxLength(512);
            builder.Property(entity => entity.State)
                .HasColumnName(snakeCase ? "state" : "State")
                .HasColumnType("jsonb");
            builder.Property(entity => entity.UpdatedOnUtc)
                .HasColumnName(snakeCase ? "updated_on_utc" : "UpdatedOnUtc");
            builder.Property(entity => entity.Version)
                .HasColumnName(snakeCase ? "version" : "Version")
                .IsConcurrencyToken();
        });
        return modelBuilder;
    }
}
