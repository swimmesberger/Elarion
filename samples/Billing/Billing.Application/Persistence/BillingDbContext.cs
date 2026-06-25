using Elarion.EntityFrameworkCore;
using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Persistence;

/// <summary>The application's data-access context. The database is application logic, not an abstraction, so
/// handlers inject this concrete context directly (LINQ, raw SQL, provider functions) — there is no
/// `IAppDbContext` interface in front of it. It lives in the shared <c>Persistence</c> layer beside the
/// <c>[EntityConfiguration]</c> classes and the migrations. <c>[GenerateDbSets]</c> fills this partial class
/// with a <c>DbSet&lt;T&gt;</c> per configured entity and a <c>ConfigureEntities</c> call; only provider
/// registration (<c>UseNpgsql</c> + connection string) is a host concern.</summary>
[GenerateDbSets]
public sealed partial class BillingDbContext(DbContextOptions<BillingDbContext> options)
    : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        ConfigureEntities(modelBuilder);   // generated — applies every discovered IEntityTypeConfiguration<T>
        modelBuilder.UseElarionOutbox();   // integration-event outbox table (Elarion.Messaging.Outbox)
    }
}
