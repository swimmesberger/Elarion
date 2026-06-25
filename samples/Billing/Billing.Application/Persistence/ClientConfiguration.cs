using Billing.Application.Domain;
using Elarion.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Billing.Application.Modules.Clients;

/// <summary>Schema rules for <see cref="Client"/>, owned by the module and colocated with its handlers.
/// The <c>[EntityConfiguration]</c> marker makes this the single source of truth for <see cref="Client"/>:
/// it drives both the generated <c>DbSet&lt;Client&gt;</c> and the direct <c>Configure(...)</c> call.
/// Safe across the module boundary because it references only the shared-kernel <see cref="Client"/>
/// entity, and ELMOD002 inspects only constructor/field/property surface — never a method body like
/// <c>Configure</c>.</summary>
[EntityConfiguration]
public sealed class ClientConfiguration : IEntityTypeConfiguration<Client> {
    public void Configure(EntityTypeBuilder<Client> builder) {
        builder.HasKey(c => c.Id);
        builder.HasIndex(c => new { c.OwnerId, c.Number }).IsUnique();
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Email).HasMaxLength(320);
    }
}
