using Billing.Application.Domain;
using Elarion.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Billing.Application.Persistence;

/// <summary>Schema rules for <see cref="Client"/>. Configuration is part of the application's shared data
/// layer (the database is application logic), not feature-owned, so it lives in the shared
/// <c>Persistence</c> layer (under no <c>[AppModule]</c>), not inside a module. The
/// <c>[EntityConfiguration]</c> marker makes this the single source of truth for <see cref="Client"/>: it
/// drives both the generated <c>DbSet&lt;Client&gt;</c> and the direct <c>Configure(...)</c> call.</summary>
[EntityConfiguration]
public sealed class ClientConfiguration : IEntityTypeConfiguration<Client> {
    public void Configure(EntityTypeBuilder<Client> builder) {
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => new { c.OwnerId, c.Number }).IsUnique();
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Email).HasMaxLength(320);
    }
}
