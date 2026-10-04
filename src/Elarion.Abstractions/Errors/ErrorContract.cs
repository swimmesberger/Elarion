namespace Elarion.Abstractions;

/// <summary>
/// One declared failure of an operation: a stable <see cref="Code"/>, the <see cref="Kind"/> it fails with and
/// the optional payload type carried as <see cref="AppError.Data"/>. The generator collects these from
/// <see cref="ProducesErrorAttribute"/> and from the framework behaviours attached to the handler (validation,
/// authorization, feature gates, idempotency) and publishes them with the operation, so the schema export and
/// the generated clients describe every failure the operation can report.
/// </summary>
public sealed record ErrorContract {
    /// <summary>The stable machine-readable code (see <see cref="ErrorCodes"/>).</summary>
    public required string Code { get; init; }

    /// <summary>The kind the error fails with; it drives the transport status mapping.</summary>
    public required ErrorKind Kind { get; init; }

    /// <summary>The payload type carried as <see cref="AppError.Data"/>, or <see langword="null"/> when the error has none.</summary>
    public Type? DataType { get; init; }
}
