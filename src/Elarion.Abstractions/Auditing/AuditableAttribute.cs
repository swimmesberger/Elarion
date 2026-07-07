namespace Elarion.Abstractions.Auditing;

/// <summary>
/// Marks a handler for audit recording: every invocation produces one structured <see cref="AuditRecord"/>
/// (who performed which action on which resource, when, with what outcome). Declarative, transport-neutral,
/// and provider-agnostic — the same shape as <c>[Cacheable]</c>/<c>[FeatureGate]</c>/<c>[Idempotent]</c>.
/// </summary>
/// <remarks>
/// The handler generator attaches the audit decorators when this attribute is present (or when the assembly
/// opts into <see cref="ElarionAuditDefaultsAttribute"/>, under which <c>[Auditable(Enabled = false)]</c>
/// opts a handler back out). Attachment is soft: with no <see cref="IAuditTrail"/> registered the pipeline
/// is unchanged. A successful command's record commits atomically with the handler's business writes; denied
/// and failed attempts are recorded on a detached path that survives the rollback (ADR-0045).
/// </remarks>
/// <example>
/// <code>
/// [Auditable(Resource = "property")]
/// public sealed class UpdatePropertyHandler : IHandler&lt;UpdatePropertyCommand, Result&lt;PropertyResponse&gt;&gt; { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class AuditableAttribute : Attribute {
    /// <summary>
    /// Whether the handler is audited. Defaults to <see langword="true"/>; set <see langword="false"/> to opt
    /// a handler out under <see cref="ElarionAuditDefaultsAttribute"/>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The resource type recorded when the handler does not set one at runtime via
    /// <see cref="IAuditScope.SetResource"/> (e.g. <c>"property"</c>). Optional.
    /// </summary>
    public string? Resource { get; init; }
}
