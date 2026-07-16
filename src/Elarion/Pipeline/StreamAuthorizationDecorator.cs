using System.Reflection;
using System.Runtime.CompilerServices;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Pipeline;
using Elarion.Diagnostics;
using System.Diagnostics;

namespace Elarion.Pipeline;

/// <summary>Applies handler authorization before a stream is accepted.</summary>
public sealed class StreamAuthorizationDecorator<TRequest, TItem>(
    IStreamHandler<TRequest, TItem> inner,
    StreamHandlerMetadata metadata,
    IAuthorizer authorizer,
    bool requireAuthenticatedByDefault = false,
    IReadOnlyList<ResourceRequirementBinding<TRequest>>? resourceBindings = null
) : IStreamHandler<TRequest, TItem> {
    public async ValueTask<Result<IAsyncEnumerable<TItem>>> HandleAsync(TRequest request, CancellationToken ct) {
        var type = metadata.HandlerType;
        var allowAnonymous = type.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;
        var requirements = new AuthorizationRequirements(
            allowAnonymous,
            requireAuthenticatedByDefault,
            type.GetCustomAttributes<RequirePermissionAttribute>(inherit: true).Select(x => x.Permission).ToArray(),
            type.GetCustomAttributes<RequireRoleAttribute>(inherit: true).Select(x => x.Role).ToArray(),
            type.GetCustomAttributes<RequireClaimAttribute>(inherit: true).ToArray(),
            type.GetCustomAttributes<RequirePolicyAttribute>(inherit: true).Select(x => x.Policy).ToArray(),
            resourceBindings?.Select(binding => new ResourceRequirement(binding.ResourceType, binding.ResourceTypeName, binding.Operation, binding.IdSelector(request))).ToArray() ?? []);
        var error = await authorizer.AuthorizeAsync(requirements, request, ct).ConfigureAwait(false);
        if (error is null)
            return await inner.HandleAsync(request, ct).ConfigureAwait(false);

        var outcome = error.Kind == ErrorKind.Unauthorized ? "unauthorized" : "forbidden";
        Activity.Current?.SetTag("elarion.authorization.outcome", outcome);
        HandlerTelemetry.RecordAuthorizationDenied(metadata.HandlerType.Name, outcome);
        return error;
    }

}
