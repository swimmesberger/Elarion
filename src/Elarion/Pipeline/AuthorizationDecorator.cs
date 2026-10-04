using System.Diagnostics;
using Elarion.Abstractions.Pipeline;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Diagnostics;

namespace Elarion.Pipeline;

/// <summary>
/// Enforces the authorization requirements declared on a handler (<c>[RequirePermission]</c>,
/// <c>[RequireRole]</c>, <c>[RequireClaim]</c>, <c>[RequirePolicy]</c>, <c>[AllowAnonymous]</c>) plus any
/// default-authorization policy in scope. The requirements are read off the concrete handler type via
/// <see cref="HandlerMetadata"/> — never <c>inner.GetType()</c> — so the guard is correct at any position
/// in the decorator chain (the generator places it as the outermost functional gate, so denied requests
/// never touch caching, the pipeline, or the handler). A failed requirement short-circuits with
/// <see cref="AppError.Unauthorized(string, string, object)"/> (unauthenticated) or <see cref="AppError.Forbidden(string, string, object)"/>.
/// </summary>
/// <remarks>
/// <paramref name="requireAuthenticatedByDefault"/> is supplied by the source generator when a
/// <see cref="ElarionAuthorizationDefaultsAttribute"/> is in scope for the handler; it cannot be derived
/// from the handler's own attributes (it comes from assembly/module scope), so the generator computes it.
/// </remarks>
public sealed class AuthorizationDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    HandlerMetadata metadata,
    IAuthorizer authorizer,
    bool requireAuthenticatedByDefault = false,
    IReadOnlyList<ResourceRequirementBinding<TRequest>>? resourceBindings = null
) : IHandler<TRequest, TResponse>
    where TResponse : IResultFailureFactory<TResponse> {
    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var requirements = ResolveRequirements();

        // Resource ids are per-call state, so they are resolved here from the generated typed accessors (never
        // parsed at run time) and merged into the requirements the authorizer evaluates.
        if (resourceBindings is { Count: > 0 }) {
            var resolved = new ResourceRequirement[resourceBindings.Count];
            for (var i = 0; i < resourceBindings.Count; i++) {
                var binding = resourceBindings[i];
                resolved[i] = new ResourceRequirement(
                    binding.ResourceType, binding.ResourceTypeName, binding.Operation, binding.IdSelector(request));
            }

            requirements = requirements with { Resources = resolved };
        }

        var error = await authorizer.AuthorizeAsync(requirements, request, ct).ConfigureAwait(false);
        if (error is not null) {
            // Denials are a security-relevant signal: tag the handler span and count by outcome so
            // 401/403 rates are observable per handler without logging caller identity. The outcome follows
            // the denial's kind — a global rule may answer NotFound — and ErrorKind is a closed enum, so
            // cardinality stays bounded.
            var outcome = HandlerTelemetry.AuthorizationOutcome(error.Kind);
            Activity.Current?.SetTag("elarion.authorization.outcome", outcome);
            HandlerTelemetry.RecordAuthorizationDenied(metadata.HandlerType.Name, outcome);
            return TResponse.Failure(error);
        }

        return await inner.HandleAsync(request, ct).ConfigureAwait(false);
    }

    private AuthorizationRequirements ResolveRequirements() {
        return HandlerAuthorizationRequirements.Resolve(metadata, requireAuthenticatedByDefault);
    }
}
