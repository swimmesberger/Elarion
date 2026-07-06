using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Actors.Runtime;

/// <summary>Shared services every actor host needs, bundled to keep registration signatures stable.</summary>
internal sealed record ActorRuntime(
    IServiceScopeFactory ScopeFactory,
    TimeProvider TimeProvider,
    ILoggerFactory LoggerFactory,
    ActorCancellationPool CancellationPool);
