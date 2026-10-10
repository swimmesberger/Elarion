using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Elarion.EntityFrameworkCore.LeasedWork;

/// <summary>
/// Maps the lease columns and the partial claim index of a leased work table (ADR-0073).
/// </summary>
public static class LeasedWorkModelBuilderExtensions {
    /// <summary>
    /// Maps <see cref="ILeasedWorkRow.LockId"/> and <see cref="ILeasedWorkRow.LockedUntilUtc"/> and declares the
    /// claim index as a <b>partial</b> index over the live queue only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter is required because the claim path must stay a probe over the pending rows while completed history
    /// accumulates; a full index degrades exactly as the table grows. If the table keeps completed rows for a
    /// retention period, declare the complementary purge index yourself.
    /// </para>
    /// <para>
    /// Call it after <c>ToTable</c> when <paramref name="claimIndexName"/> is omitted: the default name is derived
    /// from the mapped table name (<c>ix_{table}_claim</c>, or <c>IX_{table}_Claim</c> without snake case).
    /// </para>
    /// </remarks>
    /// <param name="builder">The entity type builder of the leased work row.</param>
    /// <param name="claimIndex">
    /// The claim index columns in order: the columns the eligibility filter narrows by equality (if any), then the
    /// queue-order column, then <see cref="ILeasedWorkRow.Id"/> — matching
    /// <see cref="LeasedWorkClaim{TRow}.OrderBy"/>.
    /// </param>
    /// <param name="pendingFilter">The SQL filter selecting the live (not yet completed) rows, for example
    /// <c>processed_on_utc IS NULL</c>.</param>
    /// <param name="snakeCase">Whether the lease columns use snake_case names. Defaults to <see langword="true"/>.</param>
    /// <param name="claimIndexName">The claim index name, or <see langword="null"/> for the table-derived default.</param>
    /// <typeparam name="TRow">The leased work row type.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    /// <example>
    /// <code>
    /// modelBuilder.Entity&lt;Delivery&gt;(entity => {
    ///     entity.ToTable("deliveries");
    ///     entity.HasLeasedWork(d => new { d.CreatedAtUtc, d.Id }, "completed_at_utc IS NULL");
    /// });
    /// </code>
    /// </example>
    public static EntityTypeBuilder<TRow> HasLeasedWork<TRow>(
        this EntityTypeBuilder<TRow> builder,
        Expression<Func<TRow, object?>> claimIndex,
        string pendingFilter,
        bool snakeCase = true,
        string? claimIndexName = null)
        where TRow : class, ILeasedWorkRow {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(claimIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingFilter);

        // By name, not by expression: the interface members resolve to the entity's own mapped CLR properties, and
        // a type mismatch on the entity fails here at model build rather than at the first claim.
        builder.Property<Guid?>(nameof(ILeasedWorkRow.LockId))
            .HasColumnName(snakeCase ? "lock_id" : "LockId");
        builder.Property<DateTimeOffset?>(nameof(ILeasedWorkRow.LockedUntilUtc))
            .HasColumnName(snakeCase ? "locked_until_utc" : "LockedUntilUtc");

        var indexName = claimIndexName;
        if (indexName is null) {
            var table = builder.Metadata.GetTableName()
                        ?? throw new InvalidOperationException(
                            $"{typeof(TRow).Name} is not mapped to a table. Call ToTable before HasLeasedWork or pass a claim index name.");
            indexName = snakeCase ? $"ix_{table}_claim" : $"IX_{table}_Claim";
        }

        builder.HasIndex(claimIndex)
            .HasDatabaseName(indexName)
            .HasFilter(pendingFilter);

        return builder;
    }
}
