using Billing.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Billing.Application.Modules.Invoicing;

/// <summary>Schema rules for <see cref="Invoice"/>, owned by the Invoicing module and colocated with its
/// handlers. Stores the status as a string and indexes the per-owner overdue lookup the nightly job uses.</summary>
public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice> {
    public void Configure(EntityTypeBuilder<Invoice> builder) {
        builder.HasKey(i => i.Id);
        builder.HasIndex(i => new { i.OwnerId, i.Number }).IsUnique();
        builder.HasIndex(i => new { i.OwnerId, i.Status, i.DueDate });
        builder.Property(i => i.Currency).HasMaxLength(3);
        builder.Property(i => i.Status).HasConversion<string>();
    }
}
