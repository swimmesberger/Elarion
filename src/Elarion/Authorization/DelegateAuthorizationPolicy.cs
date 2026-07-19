using Elarion.Abstractions.Authorization;

namespace Elarion.Authorization;

/// <summary>
/// An <see cref="IAuthorizationPolicy"/> backed by a delegate, for simple inline policies registered via
/// <see cref="AuthorizationServiceCollectionExtensions.AddElarionAuthorizationPolicy(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, System.Func{AuthorizationContext, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask{bool}})"/>.
/// The name is supplied by the registration (carried on <see cref="NamedAuthorizationPolicy"/>), not here.
/// </summary>
internal sealed class DelegateAuthorizationPolicy(
    Func<AuthorizationContext, CancellationToken, ValueTask<bool>> evaluate) : IAuthorizationPolicy {
    /// <inheritdoc />
    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct) {
        return evaluate(context, ct);
    }
}
