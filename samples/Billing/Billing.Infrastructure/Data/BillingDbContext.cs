using Billing.Application;
using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Billing.Infrastructure.Data;

/// <summary>The concrete context implementing <see cref="IAppDbContext"/>. The EF Core generator emits
/// the matching <c>DbSet</c> properties and a <c>ConfigureEntities</c> call into this partial class —
/// do not add <c>[GenerateDbSets]</c> here (class-side generation is inferred from the interface).</summary>
public sealed partial class BillingDbContext(DbContextOptions<BillingDbContext> options)
    : DbContext(options), IAppDbContext {
    public DbContext AsDbContext() => this;

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        ConfigureEntities(modelBuilder);   // generated — applies every discovered IEntityTypeConfiguration<T>
        modelBuilder.UseElarionOutbox();   // integration-event outbox table (Elarion.Messaging.Outbox)
    }
}
