using Billing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Billing.Application.Data;

/// <summary>Schema rules for <see cref="Client"/>. The generator discovers
/// <see cref="IEntityTypeConfiguration{TEntity}"/> implementations and emits direct
/// <c>Configure(...)</c> calls — no <c>ApplyConfigurationsFromAssembly</c> reflection.</summary>
public sealed class ClientConfiguration : IEntityTypeConfiguration<Client> {
    public void Configure(EntityTypeBuilder<Client> builder) {
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => c.Email).IsUnique();
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Email).HasMaxLength(320);
    }
}
