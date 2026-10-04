using System.Diagnostics;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Pipeline;
using Elarion.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Pipeline;

/// <summary>
/// The <see cref="IHandlerGate"/> of a handler with an authorization decorator: evaluates the request-independent
/// part of the same requirements (authentication, permissions, roles, claims) through
/// <see cref="IAuthorizer.AuthorizeGateAsync"/>, so a transport that cannot bind the payload still answers an
/// unauthenticated or forbidden caller with the authorization error. A denial is counted and tagged exactly like the
/// decorator's, because a request that fails to bind never reaches the decorator.
/// </summary>
/// <param name="metadata">The handler's compile-time facts, supplied by the generated registration.</param>
/// <param name="requireAuthenticatedByDefault">Whether an <c>[ElarionAuthorizationDefaults]</c> policy is in scope.</param>
public sealed class AuthorizationGate(HandlerMetadata metadata, bool requireAuthenticatedByDefault = false)
    : IHandlerGate {
    /// <inheritdoc />
    public async ValueTask<AppError?> EvaluateAsync(IServiceProvider scope, CancellationToken ct) {
        var requirements = HandlerAuthorizationRequirements.Resolve(metadata, requireAuthenticatedByDefault);
        var error = await scope.GetRequiredService<IAuthorizer>().AuthorizeGateAsync(requirements, ct)
            .ConfigureAwait(false);
        if (error is null) return null;

        var outcome = HandlerTelemetry.AuthorizationOutcome(error.Kind);
        Activity.Current?.SetTag("elarion.authorization.outcome", outcome);
        HandlerTelemetry.RecordAuthorizationDenied(metadata.HandlerType.Name, outcome);
        return error;
    }
}
