using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Billing.Application.Persistence;

/// <summary>Design-time factory so <c>dotnet ef migrations</c> can build the context without launching
/// the host. The connection string here is only used to pick the provider's SQL dialect when scaffolding
/// migrations — no database connection is opened at <c>migrations add</c> time.</summary>
public sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext> {
    public BillingDbContext CreateDbContext(string[] args) {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=billing;Username=postgres;Password=postgres")
            .Options;
        return new BillingDbContext(options);
    }
}
