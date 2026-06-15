namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// Declares the decorator types for a pipeline profile attribute.
/// Applied to a pipeline attribute class so the source generator can read
/// the decorator list at compile time.
/// </summary>
/// <example>
/// <code>
/// [DecoratorList(typeof(TransactionDecorator&lt;,&gt;), typeof(ValidationDecorator&lt;,&gt;))]
/// [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
/// public sealed class DefaultPipelineAttribute : Attribute { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class DecoratorListAttribute(params Type[] decorators) : Attribute {
    /// <summary>
    /// The ordered list of decorator types (open generics) to wrap around the handler.
    /// Applied outermost-first: the first type in the array is the outermost decorator.
    /// </summary>
    public Type[] Decorators { get; } = decorators;
}
