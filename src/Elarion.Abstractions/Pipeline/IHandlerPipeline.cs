namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// The decorators actually wrapping a handler in this process, in execution order (outermost first — the order
/// a request passes through them on the way in). Reached from <see cref="HandlerMetadata.Pipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the rest of <see cref="HandlerMetadata"/> — which is pure compile-time truth — this is a
/// <b>runtime-resolved</b> view, and it carries two deliberate caveats:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Empty until the handler is first resolved.</b> The pipeline is composed the first time the handler is
/// built from DI (some decorators attach based on whether a service is registered, which is only known then).
/// Before that first resolution <see cref="Steps"/> is empty.
/// </description></item>
/// <item><description>
/// <b>Reflects one composition per process.</b> The resolved pipeline is cached per handler for the process.
/// If two DI containers in the same process register the soft-attach services (an audit trail, an idempotency
/// store) differently, the cache reflects whichever container resolved the handler first — an edge that only
/// arises in multi-container test hosts, never a normal single-container app.
/// </description></item>
/// </list>
/// </remarks>
public interface IHandlerPipeline {
    /// <summary>The attached decorators, outermost first. Empty until the handler is first resolved.</summary>
    IReadOnlyList<PipelineStep> Steps { get; }

    /// <summary>
    /// Whether a decorator with the given <b>open generic</b> definition is attached, e.g.
    /// <c>Contains(typeof(TransactionDecorator&lt;,&gt;))</c>.
    /// </summary>
    bool Contains(Type decoratorDefinition);
}

/// <summary>
/// Default <see cref="IHandlerPipeline"/> over a late-bound accessor onto the generator's per-handler pipeline
/// cache. The accessor is read on each access (returning the cached list reference, no allocation), so it
/// reflects the empty-then-populated lifecycle without <see cref="HandlerMetadata"/> holding mutable state.
/// </summary>
internal sealed class HandlerPipeline(Func<IReadOnlyList<PipelineStep>>? accessor) : IHandlerPipeline {
    public IReadOnlyList<PipelineStep> Steps => accessor?.Invoke() ?? [];

    public bool Contains(Type decoratorDefinition) {
        ArgumentNullException.ThrowIfNull(decoratorDefinition);
        var steps = Steps;
        for (var i = 0; i < steps.Count; i++)
            if (steps[i].Decorator == decoratorDefinition)
                return true;

        return false;
    }
}
