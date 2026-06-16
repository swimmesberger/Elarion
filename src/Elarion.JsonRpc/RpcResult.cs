namespace Elarion.JsonRpc;

/// <summary>
/// Strongly typed result of a JSON-RPC method invocation — either a <typeparamref name="T"/> success
/// value or an <see cref="RpcError"/>. This is the return type expected from method handler delegates,
/// mirroring ASP.NET Core's <c>TypedResults</c> pattern where compile-time type information is preserved
/// for schema generation and type safety.
/// </summary>
/// <typeparam name="T">The success value type returned by the handler.</typeparam>
/// <example>
/// <code>
/// // Success
/// return RpcResult&lt;MyResponse&gt;.Success(new MyResponse(42));
///
/// // Failure
/// return RpcResult&lt;MyResponse&gt;.Failure(RpcError.InvalidParams("Name is required"));
/// </code>
/// </example>
public readonly struct RpcResult<T> {
    private readonly T? _value;
    private readonly RpcError? _error;

    private RpcResult(T? value, RpcError? error) {
        _value = value;
        _error = error;
    }

    /// <summary>Whether the invocation succeeded.</summary>
    public bool IsSuccess => _error is null;

    /// <summary>The success value. Only valid when <see cref="IsSuccess"/> is <see langword="true"/>.</summary>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Cannot access Value on a failed result.");

    /// <summary>The error. Only valid when <see cref="IsSuccess"/> is <see langword="false"/>.</summary>
    public RpcError Error => _error ?? throw new InvalidOperationException("Cannot access Error on a successful result.");

    /// <summary>Creates a successful result with the given value.</summary>
    public static RpcResult<T> Success(T value) => new(value, null);

    /// <summary>Creates a failed result with the given error.</summary>
    public static RpcResult<T> Failure(RpcError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Creates a failed result with the given code, message, and optional data.</summary>
    public static RpcResult<T> Failure(int code, string message, object? data = null) =>
        new(default, new RpcError { Code = code, Message = message, Data = data });

    /// <summary>
    /// Converts this typed result to the type-erased <see cref="RpcResult"/> used internally
    /// by the dispatcher pipeline. The success value is boxed to <see langword="object"/>.
    /// </summary>
    internal RpcResult ToUntyped() =>
        IsSuccess ? RpcResult.Success(_value) : RpcResult.Failure(Error);
}

/// <summary>
/// Type-erased result used internally by the dispatcher pipeline.
/// Handlers should return <see cref="RpcResult{T}"/> instead for compile-time type safety.
/// </summary>
internal readonly struct RpcResult {
    private readonly object? _value;
    private readonly RpcError? _error;

    private RpcResult(object? value, RpcError? error) {
        _value = value;
        _error = error;
    }

    /// <summary>Whether the invocation succeeded.</summary>
    public bool IsSuccess => _error is null;

    /// <summary>The success value. Only valid when <see cref="IsSuccess"/> is <see langword="true"/>.</summary>
    public object? Value => _value;

    /// <summary>The error. Only valid when <see cref="IsSuccess"/> is <see langword="false"/>.</summary>
    public RpcError Error => _error ?? throw new InvalidOperationException("Cannot access Error on a successful result.");

    /// <summary>Creates a successful result with the given value.</summary>
    public static RpcResult Success(object? value = null) => new(value, null);

    /// <summary>Creates a failed result with the given error.</summary>
    public static RpcResult Failure(RpcError error) => new(null, error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Creates a failed result with the given code, message, and optional data.</summary>
    public static RpcResult Failure(int code, string message, object? data = null) =>
        new(null, new RpcError { Code = code, Message = message, Data = data });
}
