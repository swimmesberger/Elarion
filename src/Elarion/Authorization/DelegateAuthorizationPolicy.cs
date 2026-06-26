using Elarion.Abstractions.Authorization;

namespace Elarion.Authorization;

/// <summary>
/// An <see cref="IAuthorizationPolicy"/> backed by a delegate, for simple inline policies registered via
/// <see cref="AuthorizationServiceCollectionExtensions.AddElarionAuthorizationPolicy(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, System.Func{AuthorizationContext, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask{bool}})"/>.
/// </summary>
internal sealed class DelegateAuthorizationPolicy(
    string name,
    Func<AuthorizationContext, CancellationToken, ValueTask<bool>> evaluate) : IAuthorizationPolicy {
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct) =>
        evaluate(context, ct);
}
