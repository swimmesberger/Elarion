using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Modules;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Session;

/// <summary>
/// Wires the framework-shipped client-capability bootstrap (<see cref="SessionHandler"/>) — the DI registration and
/// the named-bus exposure. REST exposure is a separate concrete <c>MapElarionSession</c> in <c>Elarion.AspNetCore</c>
/// (ASP.NET's source generator needs a concrete call — see <c>ADR-0031</c>). The capability composes existing seams
/// only, so a host opts in and chooses which surfaces it wants.
/// </summary>
public static class ElarionSessionServiceCollectionExtensions {
    /// <summary>The operation name the session bootstrap is exposed under on the named bus (JSON-RPC / MCP).</summary>
    public const string OperationName = "elarion.session";

    /// <summary>
    /// Registers the session bootstrap handler and the deployment <paramref name="manifest"/> (typically
    /// <c>configuration.GetClientCapabilityManifest()</c> from the generated bootstrapper). Idempotent; the handler's
    /// flag/variant and authorization-options dependencies are all optional, so this works whether or not the host
    /// uses feature flags or authorization.
    /// </summary>
    /// <remarks>
    /// Also contributes the framework-owned <see cref="SessionJsonContext"/> to the canonical
    /// <c>IElarionJsonSerialization</c> (ADR-0023), so the session's wire types serialize AOT-safely on every
    /// transport with no host wiring — the same self-registration every other subsystem's <c>Add…</c> performs.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionSession(builder.Configuration.GetClientCapabilityManifest());
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionSession(
        this IServiceCollection services, ClientCapabilityManifest manifest) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(manifest);

        services.TryAddSingleton(manifest);
        services.TryAddScoped<IHandler<SessionRequest, Result<SessionResponse>>, SessionHandler>();

        // The session's wire types are framework-owned, so this capability contributes its own source-generated JSON
        // context — the host never wires serialization for a framework feature (ADR-0023). Registered via
        // ConfigureElarionJson (which self-registers the accessor); a duplicate host contribution is harmless
        // (first-match-wins over identical type infos).
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SessionJsonContext.Default));
        return services;
    }

    /// <summary>
    /// Maps the session bootstrap onto the named bus under <see cref="OperationName"/> for the given
    /// <paramref name="transports"/> (JSON-RPC and MCP by default). Chain it into the host's <c>RegisterHandlers</c>
    /// delegate so it joins the shared dispatcher — see <c>ADR-0031</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionJsonRpc(
    ///     serializerOptions,
    ///     (dispatcher, configuration) => ElarionBootstrapper.RegisterHandlers(dispatcher, configuration).MapElarionSession());
    /// </code>
    /// </example>
    public static HandlerDispatcher MapElarionSession(
        this HandlerDispatcher dispatcher,
        HandlerTransports transports = HandlerTransports.All) {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return dispatcher.Map<SessionRequest, SessionResponse>(OperationName, transports);
    }
}
