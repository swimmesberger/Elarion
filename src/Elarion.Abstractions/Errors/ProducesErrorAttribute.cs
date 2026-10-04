namespace Elarion.Abstractions;

/// <summary>
/// Declares a failure a handler can produce: a stable error code, the <see cref="ErrorKind"/> it fails with and the
/// optional typed payload carried as <see cref="AppError.Data"/>. Declared errors are part of the operation's
/// contract: the source generator publishes them with the operation, the schema export lists them per method, and
/// the generated TypeScript client types <c>error.code</c> and <c>error.data</c> from them.
/// </summary>
/// <remarks>
/// Declare the specific codes a client is expected to branch on, and the kind-default codes (for example
/// <c>[ProducesError(ErrorKind.NotFound)]</c>) the handler returns. Failures that the framework pipeline adds to a
/// handler (request validation, authorization, feature gates, idempotency) are declared implicitly. A handler that
/// returns an undeclared code is reported at runtime in development (see <c>IErrorContractMonitor</c>).
/// </remarks>
/// <example>
/// <code>
/// [Handler]
/// [ProducesError("token.malformed", ErrorKind.Validation, typeof(TokenProblem))]
/// [ProducesError(ErrorKind.NotFound)]
/// public sealed class RedeemToken(...) : IHandler&lt;RedeemToken.Command, Result&lt;RedeemToken.Response&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
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
