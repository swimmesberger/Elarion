using Elarion.Abstractions;

namespace Elarion.Pipeline;

/// <summary>
/// Declares the <see cref="UploadLimitsAttribute"/> limits of the request's uploaded files to the scoped
/// <see cref="IDeclaredUploadConstraints"/> before the handler runs, so every read and keep of those files through
/// <see cref="IUploadedFiles"/> checks them without the handler repeating them. The generator attaches it, innermost,
/// to every handler whose request declares such limits, and supplies the reflection-free function that walks the
/// request's upload members.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class UploadLimitsDecorator<TRequest, TResponse> : IHandler<TRequest, TResponse> {
    private readonly IHandler<TRequest, TResponse> _inner;
    private readonly IDeclaredUploadConstraints _declarations;
    private readonly Action<TRequest, IDeclaredUploadConstraints> _declare;

    /// <summary>Creates the decorator. Called by generated pipeline code.</summary>
    /// <param name="inner">The handler.</param>
    /// <param name="declarations">
    /// The scope's declared constraints; <see langword="null"/> when the host registered no
    /// <see cref="IUploadedFiles"/> that applies them, which refuses the handler rather than reading its files
    /// unchecked.
    /// </param>
    /// <param name="declare">The generated function that declares the limits of every upload member of a request.</param>
    /// <exception cref="InvalidOperationException"><paramref name="declarations"/> is <see langword="null"/>.</exception>
    public UploadLimitsDecorator(
        IHandler<TRequest, TResponse> inner,
        IDeclaredUploadConstraints? declarations,
        Action<TRequest, IDeclaredUploadConstraints> declare) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(declare);
        _inner = inner;
        _declarations = declarations ?? throw new InvalidOperationException(
            $"The request '{typeof(TRequest)}' declares [UploadLimits], but no IDeclaredUploadConstraints is "
            + "registered to apply them. Register the uploaded-file reader with AddElarionUploadedFiles (Elarion.Blobs), "
            + "or an IUploadedFiles that also implements IDeclaredUploadConstraints.");
        _declare = declare;
    }

    /// <inheritdoc />
    public ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        _declare(request, _declarations);
        return _inner.HandleAsync(request, ct);
    }
}
