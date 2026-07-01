using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Idempotency;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Idempotency;

/// <summary>
/// Seeds the per-call <see cref="ScopedIdempotencyKeyAccessor"/> from the <see cref="IdempotencyKey"/> captured
/// in the <see cref="DispatchScopeContext"/>, so <see cref="IIdempotencyKeyAccessor"/> resolves inside the
/// dispatch scope. Transport-neutral: every transport captures the key at its boundary (HTTP header, JSON-RPC/MCP
/// <c>_meta</c>) and this one initializer applies it. Mirrors <c>CurrentUserScopeInitializer</c>.
/// </summary>
internal sealed class IdempotencyKeyScopeInitializer : IDispatchScopeInitializer {
    /// <inheritdoc />
    public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
        if (context.TryGet<IdempotencyKey>(out var captured) && captured is not null) {
            // GetService (not GetRequired): a host may have replaced the accessor without the default.
            callScope.GetService<ScopedIdempotencyKeyAccessor>()?.Seed(captured.Value);
        }
    }
}
