namespace Elarion.Abstractions.Auditing;

/// <summary>
/// Excludes a property of an <see cref="AuditedAttribute">[Audited]</see> entity from automatic audit change
/// capture — its old/new values never appear in an <see cref="AuditRecord"/>. Use for sensitive columns
/// (password hashes, tokens, personal data) and for noisy technical columns (row versions, denormalized
/// counters).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class AuditIgnoreAttribute : Attribute;
