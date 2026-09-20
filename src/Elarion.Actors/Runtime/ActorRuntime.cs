using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Actors.Runtime;

/// <summary>Shared services every actor host needs, bundled to keep registration signatures stable.</summary>
/// <param name="ScopeFactory">Creates the per-turn DI scope an actor method runs in.</param>
/// <param name="TimeProvider">The clock used for timers, deadlines, and idle deactivation.</param>
/// <param name="LoggerFactory">Creates the per-actor-type loggers.</param>
/// <param name="CancellationPool">Pools the per-turn cancellation sources, so a turn costs no fresh allocation.</param>
/// <param name="HomeLease">The optional single-homing lease (ADR-0048); <see langword="null"/> = unenforced.</param>
/// <param name="PlacementResolver">The optional virtual-shard placement resolver; <see langword="null"/> = unenforced.</param>
internal sealed record ActorRuntime(
    IServiceScopeFactory ScopeFactory,
    TimeProvider TimeProvider,
    ILoggerFactory LoggerFactory,
    ActorCancellationPool CancellationPool,
    IActorHomeLease? HomeLease,
    IActorPlacementResolver? PlacementResolver);
