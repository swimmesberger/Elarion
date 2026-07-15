using Elarion.Abstractions.Serialization;
using Elarion.Coordination.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Actors.PostgreSql;

/// <summary>Registers the PostgreSQL actor snapshot store (ADR-0047) and home lease (ADR-0048).</summary>
public static class ActorSnapshotServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="PostgreSqlActorSnapshotStore{TDbContext}"/> as the
    /// <see cref="IActorSnapshotStore"/> behind every <c>IActorState&lt;TState&gt;</c> (plus the
    /// <see cref="IActorStateReader"/> query-side companion).
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
        services.TryAddSingleton<IActorStateReader, ActorStateReader>();
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL actor home (ADR-0048): sugar over the generic role lease (ADR-0049)
    /// — one heartbeat-renewed <c>"actors"</c> role on the app's database elects exactly one
    /// instance as the actor home, enforcing <c>[Actor(Placement = ActorPlacementMode.SingleHome)]</c>.
    /// <typeparamref name="TDbContext"/> must map the role lease table — annotate the context with
    /// <c>[GenerateElarionRoleLeases]</c> or call <c>modelBuilder.UseElarionRoleLeases()</c>. To
    /// Generated actor event consumers record this role on their outbox target group, so
    /// only the current home can claim it (ADR-0062).
    /// </summary>
    public static IServiceCollection AddElarionPostgreSqlActorHome<TDbContext>(
        this IServiceCollection services,
        Action<RoleLeaseOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RoleLeaseOptions { RoleName = "actors" };
        configure?.Invoke(options);
        services.AddElarionPostgreSqlRoleLease<TDbContext>(options);
        services.AddElarionActorHome(options.RoleName);
        return services;
    }
}
