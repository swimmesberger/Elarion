using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Persistence;

/// <summary>The concrete context implementing <see cref="IAppDbContext"/>. It lives in the application's
/// shared <c>Persistence</c> layer — the database is application logic, not an infrastructure detail — beside
/// the <c>[EntityConfiguration]</c> classes and the migrations. The EF Core generator emits the matching
/// <c>DbSet</c> properties and a <c>ConfigureEntities</c> call into this partial class; only provider
/// registration (<c>UseNpgsql</c> + connection string) is a host concern. Do not add <c>[GenerateDbSets]</c>
/// here (class-side generation is inferred from the interface).</summary>
public sealed partial class BillingDbContext(DbContextOptions<BillingDbContext> options)
    : DbContext(options), IAppDbContext {
    public DbContext AsDbContext() => this;

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        ConfigureEntities(modelBuilder);   // generated — applies every discovered IEntityTypeConfiguration<T>
        modelBuilder.UseElarionOutbox();   // integration-event outbox table (Elarion.Messaging.Outbox)
    }
}
