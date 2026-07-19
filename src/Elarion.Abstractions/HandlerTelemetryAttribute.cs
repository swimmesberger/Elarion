namespace Elarion.Abstractions;

/// <summary>
/// Declares how much always-on observability the generated pipeline gives handlers — on one handler class, on
/// a module class (every handler the module owns), or on the assembly (every handler without a nearer
/// declaration). The nearest declaration wins: handler over module over assembly, defaulting to
/// <see cref="HandlerTelemetryMode.Full"/>.
/// </summary>
/// <remarks>
/// The typical use is a hot module opting down once — <c>[HandlerTelemetry(HandlerTelemetryMode.None)]</c> on
/// the module class — while a cold handler inside it re-enables by declaring
/// <see cref="HandlerTelemetryMode.Full"/> on itself. This is a compile-time pipeline-composition decision
/// (the decorator is simply not generated), mirroring how <c>[Idempotent]</c>/<c>[Auditable]</c> declare
/// per-handler pipeline concerns as separate attributes.
/// </remarks>
/// <example>
/// <code>
/// [AppModule]
/// [HandlerTelemetry(HandlerTelemetryMode.None)]   // the whole hot module opts down
/// public sealed class RealtimeModule;
///
/// [Handler]
/// [HandlerTelemetry(HandlerTelemetryMode.Full)]   // one cold handler opts back in
/// public sealed class ReloadConfiguration(...) : IHandler&lt;ReloadConfiguration.Command, Result&lt;Unit&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly)]
public sealed class HandlerTelemetryAttribute(HandlerTelemetryMode mode) : Attribute {
    /// <summary>The declared observability mode.</summary>
    public HandlerTelemetryMode Mode { get; } = mode;
}
