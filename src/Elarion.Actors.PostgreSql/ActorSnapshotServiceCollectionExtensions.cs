using Elarion.Abstractions.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Actors.PostgreSql;

/// <summary>Registers the PostgreSQL actor snapshot store (ADR-0047).</summary>
public static class ActorSnapshotServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="PostgreSqlActorSnapshotStore{TDbContext}"/> as the
    /// <see cref="IActorSnapshotStore"/> behind every <c>IActorState&lt;TState&gt;</c>.
    /// <typeparamref name="TDbContext"/> must map <see cref="ActorSnapshotEntity"/> — annotate the
    /// context with <c>[GenerateElarionActorSnapshots]</c> or call
    /// <c>modelBuilder.UseElarionActorSnapshots()</c>.
    /// </summary>
    public static IServiceCollection AddElarionPostgreSqlActorSnapshots<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        services.AddElarionJson();
        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<IActorSnapshotStore>();
        services.AddSingleton<IActorSnapshotStore, PostgreSqlActorSnapshotStore<TDbContext>>();
        return services;
    }
}
