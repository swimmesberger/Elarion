using Elarion.Abstractions;
using Grpc.Core;

namespace Elarion.Grpc;

/// <summary>
/// The default <see cref="IAppErrorTranslator{TError}"/> for the gRPC transport. It maps Elarion
/// <see cref="AppError"/> values to stable gRPC status codes and carries the normalized error kind in the
/// <c>elarion-error-kind</c> response trailer.
/// </summary>
/// <remarks>
/// Validation detail payloads are deliberately not serialized in phase one. The trailer preserves the error
/// category now; a future version can add a stable protobuf detail contract without changing this mapping.
/// </remarks>
public sealed class GrpcAppErrorTranslator : IAppErrorTranslator<RpcException> {
    /// <summary>The stable lower-case metadata key carrying the normalized Elarion error kind.</summary>
    public const string ErrorKindTrailerKey = "elarion-error-kind";

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
            { ErrorKindTrailerKey, kind }
        };
        return new RpcException(new Status(statusCode, error.Message), trailers);
    }
}
