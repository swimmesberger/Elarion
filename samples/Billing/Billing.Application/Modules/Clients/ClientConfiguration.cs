using Billing.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Billing.Application.Modules.Clients;

/// <summary>Schema rules for <see cref="Client"/>, owned by the module and colocated with its handlers.
/// Safe across the module boundary because it references only the shared-kernel <see cref="Client"/>
/// entity, and ELMOD002 inspects only constructor/field/property surface — never a method body like
/// <c>Configure</c>. The generator discovers <see cref="IEntityTypeConfiguration{TEntity}"/>
/// implementations wherever they live and emits direct <c>Configure(...)</c> calls.</summary>
public sealed class ClientConfiguration : IEntityTypeConfiguration<Client> {
    public void Configure(EntityTypeBuilder<Client> builder) {
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => new { c.OwnerId, c.Number }).IsUnique();
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Email).HasMaxLength(320);
    }
}
