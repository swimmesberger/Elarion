namespace Elarion.Abstractions;

/// <summary>
/// Declares a hint a client applies to a <see cref="HandlerAttribute"/> operation — a slow operation's timeout, a
/// marker the UI must honour — exported in the RPC schema's per-method <c>tags</c> and in the generated client's
/// <c>rpcMethodInfo</c>, so the client reads it from the contract instead of keeping its own list of methods.
/// </summary>
/// <remarks>
/// Tags are client hints, never enforcement: the handler pipeline does not read them. A key has one value per
/// operation; a tag on a derived handler overrides the same key on a base handler, and a key repeated on one class
/// keeps its last declaration. Key and value are free text agreed between the server and its clients.
/// </remarks>
/// <example>
/// <code>
/// [Handler, ClientTag("timeout", "180s")]
/// public sealed class ImportStatementsHandler : IHandler&lt;ImportStatements, Result&lt;ImportSummary&gt;&gt; { … }
/// </code>
/// </example>
/// <param name="key">The tag key.</param>
/// <param name="value">The tag value.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class ClientTagAttribute(string key, string value) : Attribute {
    /// <summary>The tag key.</summary>
    public string Key { get; } = key;

    /// <summary>The tag value.</summary>
    public string Value { get; } = value;
}
