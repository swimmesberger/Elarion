namespace Elarion.Abstractions;

/// <summary>
/// Declares a failure a handler can produce: a stable error code, the <see cref="ErrorKind"/> it fails with and the
/// optional typed payload carried as <see cref="AppError.Data"/>. Declared errors are part of the operation's
/// contract: the source generator publishes them with the operation, the schema export lists them per method, and
/// the generated TypeScript client types <c>error.code</c> and <c>error.data</c> from them.
/// </summary>
/// <remarks>
/// <para>
/// Declare the specific codes a client is expected to branch on, and the kind-default codes (for example
/// <c>[ProducesError(ErrorKind.NotFound)]</c>) the handler returns. Failures that the framework pipeline adds to a
/// handler (request validation, authorization, feature gates, idempotency) are declared implicitly. A handler that
/// returns an undeclared code is reported at runtime in development (see <c>IErrorContractMonitor</c>).
/// </para>
/// <para>
/// The attribute also declares failures for many operations at once. The source generator reads it from four
/// places and merges them into each operation's contract:
/// </para>
/// <list type="number">
/// <item><description>the handler class (and its base classes): the operation's own declaration;</description></item>
/// <item><description>
/// a pipeline decorator in the handler's resolved <c>[DecoratorList]</c>: every operation the decorator wraps. A
/// decorator whose generic constraints exclude the handler contributes nothing. An <c>AppliesTo</c> predicate is a
/// runtime decision, so a conditional decorator contributes to every operation it can wrap;
/// </description></item>
/// <item><description>the <c>[AppModule]</c> class: every operation in the module;</description></item>
/// <item><description>the assembly (<c>[assembly: ProducesError(...)]</c>): every operation declared in that assembly.</description></item>
/// </list>
/// <para>
/// When more than one place declares the same code, the one closest to the handler wins, in the order above, and
/// the framework's implied failures rank right after the handler's own declarations. A default therefore never
/// changes a code that the handler declares itself, for example with a typed payload.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Handler]
/// [ProducesError("token.malformed", ErrorKind.Validation, typeof(TokenProblem))]
/// [ProducesError(ErrorKind.NotFound)]
/// public sealed class RedeemToken(...) : IHandler&lt;RedeemToken.Command, Result&lt;RedeemToken.Response&gt;&gt; { ... }
///
/// // Every operation declared in this assembly may fail with the kind-default not_found and conflict codes.
/// [assembly: ProducesError(ErrorKind.NotFound)]
/// [assembly: ProducesError(ErrorKind.Conflict)]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = true)]
public sealed class ProducesErrorAttribute : Attribute {
    /// <summary>Declares the kind-default error of <paramref name="kind"/> (code <see cref="ErrorCodes.ForKind"/>) without a payload.</summary>
    public ProducesErrorAttribute(ErrorKind kind) {
        Kind = kind;
        Code = ErrorCodes.ForKind(kind);
    }

    /// <summary>Declares the kind-default error of <paramref name="kind"/> carrying a <paramref name="dataType"/> payload.</summary>
    public ProducesErrorAttribute(ErrorKind kind, Type dataType) : this(kind) {
        DataType = dataType;
    }

    /// <summary>Declares a specific error <paramref name="code"/> of <paramref name="kind"/> without a payload.</summary>
    public ProducesErrorAttribute(string code, ErrorKind kind) {
        Code = code;
        Kind = kind;
    }

    /// <summary>Declares a specific error <paramref name="code"/> of <paramref name="kind"/> carrying a <paramref name="dataType"/> payload.</summary>
    public ProducesErrorAttribute(string code, ErrorKind kind, Type dataType) : this(code, kind) {
        DataType = dataType;
    }

    /// <summary>The stable machine-readable error code.</summary>
    public string Code { get; }

    /// <summary>The kind the error fails with.</summary>
    public ErrorKind Kind { get; }

    /// <summary>The payload type carried as <see cref="AppError.Data"/>, or <see langword="null"/> when the error has none.</summary>
    public Type? DataType { get; }
}
