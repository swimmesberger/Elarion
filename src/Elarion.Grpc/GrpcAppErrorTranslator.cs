using Elarion.Abstractions;
using Grpc.Core;

namespace Elarion.Grpc;

/// <summary>
/// The default <see cref="IAppErrorTranslator{TError}"/> for the gRPC transport. It maps Elarion
/// <see cref="AppError"/> values to stable gRPC status codes and carries the normalized error kind in the
/// <c>elarion-error-kind</c> response trailer and the stable <see cref="AppError.Code"/> in the
/// <c>elarion-error-code</c> trailer, on every error.
/// </summary>
/// <remarks>
/// Typed <see cref="AppError.Data"/> payloads are deliberately not serialized in phase one. The trailers preserve
/// the error category and code now; a future version can add a stable protobuf detail contract without changing
/// this mapping. An error code is lower-case ASCII by construction, so it always travels as a text trailer.
/// </remarks>
public sealed class GrpcAppErrorTranslator : IAppErrorTranslator<RpcException> {
    /// <summary>The stable lower-case metadata key carrying the normalized Elarion error kind.</summary>
    public const string ErrorKindTrailerKey = "elarion-error-kind";

    /// <summary>The stable lower-case metadata key carrying <see cref="AppError.Code"/>; present on every error.</summary>
    public const string ErrorCodeTrailerKey = "elarion-error-code";

    /// <summary>The shared default translator instance.</summary>
    public static GrpcAppErrorTranslator Default { get; } = new();

    /// <inheritdoc />
    public RpcException Translate(AppError error) {
        ArgumentNullException.ThrowIfNull(error);

        var (statusCode, kind) = error.Kind switch {
            ErrorKind.Validation => (StatusCode.InvalidArgument, "validation"),
            ErrorKind.NotFound => (StatusCode.NotFound, "not-found"),
            ErrorKind.Conflict => (StatusCode.AlreadyExists, "conflict"),
            ErrorKind.Forbidden => (StatusCode.PermissionDenied, "forbidden"),
            ErrorKind.Unauthorized => (StatusCode.Unauthenticated, "unauthorized"),
            ErrorKind.BusinessRule => (StatusCode.FailedPrecondition, "business-rule"),
            ErrorKind.Internal => (StatusCode.Internal, "internal"),
            _ => (StatusCode.Internal, "internal")
        };

        var trailers = new Metadata {
            { ErrorKindTrailerKey, kind },
            { ErrorCodeTrailerKey, error.Code }
        };
        return new RpcException(new Status(statusCode, error.Message), trailers);
    }
}
