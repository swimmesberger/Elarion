using Billing.Application.Domain;
using Elarion.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Billing.Application.Persistence;

/// <summary>Schema for <see cref="AuditEntry"/>. Configuration is part of the application's shared data
/// layer, so it lives in <c>Persistence</c> beside the other configurations — not inside the Core module
/// that owns the recording capability. The `[EntityConfiguration]` drives both the generated
/// <c>DbSet&lt;AuditEntry&gt;</c> and this `Configure(...)`.</summary>
[EntityConfiguration]
public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry> {
    public void Configure(EntityTypeBuilder<AuditEntry> builder) {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.ActorId, e.At });
        builder.Property(e => e.Action).HasMaxLength(100);
        builder.Property(e => e.SubjectId).HasMaxLength(100);
    }
}
