using System.Reflection;

namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// Compile-time facts about a concrete <see cref="IStreamHandler{TRequest,TItem}"/> and its resolved stream
/// decorator pipeline.
/// </summary>
public sealed class StreamHandlerMetadata {
    /// <summary>Creates metadata for a concrete stream handler.</summary>
    public StreamHandlerMetadata(
        Type handlerType,
        Type requestType,
        Type itemType,
        Func<IReadOnlyList<PipelineStep>>? pipelineAccessor = null) {
        HandlerType = handlerType ?? throw new ArgumentNullException(nameof(handlerType));
        RequestType = requestType ?? throw new ArgumentNullException(nameof(requestType));
        ItemType = itemType ?? throw new ArgumentNullException(nameof(itemType));
        Pipeline = new HandlerPipeline(pipelineAccessor);
    }

    /// <summary>The concrete handler at the bottom of the stream pipeline.</summary>
    public Type HandlerType { get; }

    /// <summary>The request type.</summary>
    public Type RequestType { get; }

    /// <summary>The sequence item type; this is not a unary response type.</summary>
    public Type ItemType { get; }

    /// <summary>The decorators resolved around this stream handler in execution order.</summary>
    public IHandlerPipeline Pipeline { get; }

    /// <summary>Returns one inherited attribute of the requested type, if present.</summary>
    public TAttribute? GetAttribute<TAttribute>() where TAttribute : Attribute {
        return HandlerType.GetCustomAttribute<TAttribute>(true);
    }

    /// <summary>Returns all inherited attributes of the requested type.</summary>
    public IEnumerable<TAttribute> GetAttributes<TAttribute>() where TAttribute : Attribute {
        return HandlerType.GetCustomAttributes<TAttribute>(true);
    }
}
