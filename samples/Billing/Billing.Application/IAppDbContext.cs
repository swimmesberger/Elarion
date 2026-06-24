using Elarion.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application;

/// <summary>The data-access abstraction handlers depend on — there is no repository layer. The EF Core
/// generator fills this partial interface with a <c>DbSet&lt;T&gt;</c> for every <c>[DbEntity]</c> in the
/// shared-kernel <c>Billing.Application.Domain</c> namespace. (The manifest also discovers entities in
/// referenced assemblies, which is the path to use once a bounded context graduates to its own assembly.)</summary>
[GenerateDbSets]
public partial interface IAppDbContext {
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    DbContext AsDbContext();
}
