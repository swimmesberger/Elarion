using Elarion.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application;

/// <summary>The data-access abstraction handlers depend on — there is no repository layer. The EF Core
/// generator fills this partial interface with a <c>DbSet&lt;T&gt;</c> for every <c>[DbEntity]</c>
/// (including entities discovered in the referenced <c>Billing.Domain</c> assembly via its manifest).</summary>
[GenerateDbSets]
public partial interface IAppDbContext {
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    DbContext AsDbContext();
}
