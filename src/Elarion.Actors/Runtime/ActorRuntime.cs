using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Actors.Runtime;

/// <summary>Shared services every actor host needs, bundled to keep registration signatures stable.</summary>
/// <param name="HomeLease">The optional single-homing lease (ADR-0048); <see langword="null"/> = unenforced.</param>
internal sealed record ActorRuntime(
    IServiceScopeFactory ScopeFactory,
    TimeProvider TimeProvider,
    ILoggerFactory LoggerFactory,
    ActorCancellationPool CancellationPool,
    IActorHomeLease? HomeLease);
