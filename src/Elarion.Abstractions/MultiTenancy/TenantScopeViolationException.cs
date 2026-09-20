namespace Elarion.Abstractions.MultiTenancy;

/// <summary>
/// Thrown when a write would cross, or fall outside, the current tenant: inserting a tenant-scoped row with no
/// tenant resolved, or touching a row that belongs to a different tenant than the one in scope.
/// </summary>
/// <remarks>
/// This is a programming error surfaced as a fault rather than an <c>AppError</c>, because it means the ambient
/// isolation was bypassed — a condition no caller should handle and no client should see explained. Work that
/// legitimately spans tenants declares <see cref="ITenantContext.SystemScope"/>.
/// </remarks>
public sealed class TenantScopeViolationException : InvalidOperationException {
    /// <summary>Creates the exception with a description of the violation.</summary>
    /// <param name="message">What was attempted, and in which scope.</param>
    public TenantScopeViolationException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a description of the violation and an underlying cause.</summary>
    /// <param name="message">What was attempted, and in which scope.</param>
    /// <param name="innerException">The underlying cause.</param>
    public TenantScopeViolationException(string message, Exception innerException) : base(message, innerException) {
    }
}
