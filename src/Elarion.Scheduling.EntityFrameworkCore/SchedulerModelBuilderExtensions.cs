using Microsoft.EntityFrameworkCore;

namespace Elarion.Scheduling.EntityFrameworkCore;

/// <summary>Registers the Elarion scheduler-claims table on a <see cref="ModelBuilder"/>.</summary>
public static class SchedulerModelBuilderExtensions {
    /// <summary>
    /// Maps <see cref="SchedulerClaimEntity"/> to the <c>elarion_scheduler_claims</c> table (by default) with
    /// the composite key <c>(job_name, occurrence_utc)</c> and a purge index over the occurrence instant.
    /// Called for you by the <c>[GenerateElarionSchedulerClaims]</c> generator through the EF
    /// model-configuration seam; call it by hand in <c>OnModelCreating</c> otherwise (alongside, for example,
    /// <c>UseElarionOutbox()</c>).
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="tableName">
    /// The table name, or <see langword="null"/> for the default (<c>elarion_scheduler_claims</c> /
    /// <c>ElarionSchedulerClaims</c> depending on <paramref name="snakeCase"/>).
    /// </param>
    /// <param name="schema">The schema, or <see langword="null"/> to use the provider's default schema.</param>
    /// <param name="snakeCase">Whether to use snake_case table/column/index names. Defaults to <see langword="true"/>.</param>
    public static ModelBuilder UseElarionSchedulerClaims(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var table = tableName ?? (snakeCase ? "elarion_scheduler_claims" : "ElarionSchedulerClaims");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<SchedulerClaimEntity>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(claim => new { claim.JobName, claim.OccurrenceUtc });

            builder.Property(claim => claim.JobName).HasColumnName(snakeCase ? "job_name" : "JobName")
                .HasMaxLength(256);
            builder.Property(claim => claim.OccurrenceUtc)
                .HasColumnName(snakeCase ? "occurrence_utc" : "OccurrenceUtc");
            builder.Property(claim => claim.ClaimedAtUtc).HasColumnName(snakeCase ? "claimed_at_utc" : "ClaimedAtUtc");

            // The retention purge deletes by occurrence instant across all jobs.
            builder.HasIndex(claim => claim.OccurrenceUtc)
                .HasDatabaseName(snakeCase ? $"ix_{table}_purge" : $"IX_{table}_Purge");
        });

        return modelBuilder;
    }
}
