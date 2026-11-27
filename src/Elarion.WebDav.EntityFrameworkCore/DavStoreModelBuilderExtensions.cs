using Microsoft.EntityFrameworkCore;

namespace Elarion.WebDav.EntityFrameworkCore;

/// <summary>
/// Maps the published-collection store onto a model. Normally applied through the
/// <see cref="GenerateElarionDavStoreAttribute"/> seam; call it directly from <c>OnModelCreating</c> when the context is
/// hand-written.
/// </summary>
public static class DavStoreModelBuilderExtensions {
    /// <summary>The longest source name, key, member name or kind: 255 characters.</summary>
    public const int MaxNameLength = 255;

    /// <summary>The longest object identifier: 1,024 characters.</summary>
    public const int MaxUidLength = 1024;

    /// <summary>
    /// Adds the store's three tables: <c>elarion_dav_collections</c> (<c>id</c>, <c>kind</c>, <c>source</c>, <c>key</c>,
    /// <c>sync_seq</c>, <c>low_water</c>; unique <c>kind, source, key</c>), <c>elarion_dav_objects</c>
    /// (<c>collection_id</c>, <c>name</c>, <c>uid</c>, <c>etag</c>, <c>content</c>, <c>content_type</c>,
    /// <c>range_start</c>, <c>range_end</c>, <c>size</c>; key <c>collection_id, name</c>, unique <c>collection_id, uid</c>)
    /// and <c>elarion_dav_changes</c> (<c>collection_id</c>, <c>seq</c>, <c>name</c>, <c>op</c>, <c>changed_at</c>; key
    /// <c>collection_id, seq</c>).
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="schema">Optional schema.</param>
    /// <param name="snakeCase">Whether table and column names are snake_case (the Elarion default) or PascalCase.</param>
    public static ModelBuilder UseElarionDavStore(this ModelBuilder modelBuilder, string? schema = null, bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        string N(string snake, string pascal) => snakeCase ? snake : pascal;

        modelBuilder.Entity<DavCollectionEntity>(builder => {
            builder.ToTable(N("elarion_dav_collections", "ElarionDavCollections"), schema);
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Id).HasColumnName(N("id", "Id")).ValueGeneratedNever();
            builder.Property(entity => entity.Kind).HasColumnName(N("kind", "Kind")).HasMaxLength(MaxNameLength);
            builder.Property(entity => entity.Source).HasColumnName(N("source", "Source")).HasMaxLength(MaxNameLength);
            builder.Property(entity => entity.Key).HasColumnName(N("key", "Key")).HasMaxLength(MaxNameLength);
            builder.Property(entity => entity.SyncSeq).HasColumnName(N("sync_seq", "SyncSeq"));
            builder.Property(entity => entity.LowWater).HasColumnName(N("low_water", "LowWater"));
            builder.HasIndex(entity => new { entity.Kind, entity.Source, entity.Key }).IsUnique();
        });

        modelBuilder.Entity<DavObjectEntity>(builder => {
            builder.ToTable(N("elarion_dav_objects", "ElarionDavObjects"), schema);
            builder.HasKey(entity => new { entity.CollectionId, entity.Name });
            builder.Property(entity => entity.CollectionId).HasColumnName(N("collection_id", "CollectionId"));
            builder.Property(entity => entity.Name).HasColumnName(N("name", "Name")).HasMaxLength(MaxNameLength);
            builder.Property(entity => entity.Uid).HasColumnName(N("uid", "Uid")).HasMaxLength(MaxUidLength);
            builder.Property(entity => entity.ETag).HasColumnName(N("etag", "ETag")).HasMaxLength(64);
            builder.Property(entity => entity.Content).HasColumnName(N("content", "Content"));
            builder.Property(entity => entity.ContentType).HasColumnName(N("content_type", "ContentType")).HasMaxLength(MaxNameLength);
            builder.Property(entity => entity.RangeStart).HasColumnName(N("range_start", "RangeStart"));
            builder.Property(entity => entity.RangeEnd).HasColumnName(N("range_end", "RangeEnd"));
            builder.Property(entity => entity.Size).HasColumnName(N("size", "Size"));
            builder.HasIndex(entity => new { entity.CollectionId, entity.Uid }).IsUnique();
            // calendar-query selects its candidates by these bounds before parsing them.
            builder.HasIndex(entity => new { entity.CollectionId, entity.RangeStart, entity.RangeEnd });
            builder.HasOne<DavCollectionEntity>().WithMany().HasForeignKey(entity => entity.CollectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DavChangeEntity>(builder => {
            builder.ToTable(N("elarion_dav_changes", "ElarionDavChanges"), schema);
            builder.HasKey(entity => new { entity.CollectionId, entity.Seq });
            builder.Property(entity => entity.CollectionId).HasColumnName(N("collection_id", "CollectionId"));
            builder.Property(entity => entity.Seq).HasColumnName(N("seq", "Seq")).ValueGeneratedNever();
            builder.Property(entity => entity.Name).HasColumnName(N("name", "Name")).HasMaxLength(MaxNameLength);
            builder.Property(entity => entity.Op).HasColumnName(N("op", "Op"));
            builder.Property(entity => entity.ChangedAt).HasColumnName(N("changed_at", "ChangedAt"));
            builder.HasOne<DavCollectionEntity>().WithMany().HasForeignKey(entity => entity.CollectionId).OnDelete(DeleteBehavior.Cascade);
        });
        return modelBuilder;
    }
}
