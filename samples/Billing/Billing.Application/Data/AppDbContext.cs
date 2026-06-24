using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Data;

/// <summary>The concrete context implementing <see cref="IAppDbContext"/>. The generator emits the
/// matching <c>DbSet</c> properties and a <c>ConfigureEntities</c> call into this partial class — do
/// not add <c>[GenerateDbSets]</c> here (class-side generation is inferred from the interface).</summary>
public sealed partial class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IAppDbContext {
    public DbContext AsDbContext() => this;

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        ConfigureEntities(modelBuilder); // generated
    }
}
