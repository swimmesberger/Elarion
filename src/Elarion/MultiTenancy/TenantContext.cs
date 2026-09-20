using Elarion.Abstractions.MultiTenancy;

namespace Elarion.MultiTenancy;

/// <summary>
/// The scoped <see cref="ITenantContext"/>: resolves the tenant once per scope through
/// <see cref="ITenantResolver"/>, and carries the explicit <see cref="Scope"/> and <see cref="SystemScope"/>
/// overrides on top of it.
/// </summary>
/// <remarks>
/// <para>
/// State lives on the scoped instance rather than in an <c>AsyncLocal</c>, matching how the framework carries
/// every other per-call value (<c>ICurrentUser</c>, the dispatch scope). A scope is a unit of work, so a scope
/// is the right lifetime for "which tenant is this work for"; a background worker that iterates tenants either
/// creates a scope per tenant or brackets each one with <see cref="Scope"/>.
/// </para>
/// <para>
/// The instance is scope-affine, not thread-safe: concurrent work inside one scope that enters different
/// tenants is a bug in the caller, the same way sharing one <c>DbContext</c> across concurrent operations is.
/// </para>
/// </remarks>
/// <param name="resolver">Produces the tenant id when no explicit scope is entered.</param>
public sealed class TenantContext(ITenantResolver resolver) : ITenantContext {
    private string? _override;
    private string? _resolved;
    private bool _hasResolved;
    private int _systemDepth;

    /// <inheritdoc />
    public string? TenantId {
        get {
            if (_override is not null) return _override;
            if (_hasResolved) return _resolved;

            _resolved = resolver.Resolve();
            _hasResolved = true;
            return _resolved;
        }
    }

    /// <inheritdoc />
    public bool IsSystemScope => _systemDepth > 0;

    /// <inheritdoc />
    public IDisposable SystemScope() {
        _systemDepth++;
        return new SystemScopeHandle(this);
    }

    /// <inheritdoc />
    public IDisposable Scope(string tenantId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var previous = _override;
        _override = tenantId;
        return new TenantScopeHandle(this, previous);
    }

    private sealed class SystemScopeHandle(TenantContext owner) : IDisposable {
        private bool _disposed;

        public void Dispose() {
            if (_disposed) return;

            _disposed = true;
            owner._systemDepth--;
        }
    }

    private sealed class TenantScopeHandle(TenantContext owner, string? previous) : IDisposable {
        private bool _disposed;

        public void Dispose() {
            if (_disposed) return;

            _disposed = true;
            owner._override = previous;
        }
    }
}
