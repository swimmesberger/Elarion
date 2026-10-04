using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Dispatch;

/// <summary>
/// The runtime half of the error contract: a handler body can return any <see cref="AppError"/>, so whether it
/// stays inside its declared <see cref="ErrorContract"/>s is verified when the failure leaves the handler and
/// reported to the registered <see cref="IErrorContractMonitor"/>.
/// </summary>
/// <remarks>
/// <see cref="ErrorKind.Internal"/> failures are always admitted (an unexpected failure cannot be declared), as are
/// <see cref="ErrorKind.Unauthorized"/> and <see cref="ErrorKind.Forbidden"/> default codes — the authorization
/// pipeline can attach them through assembly or module defaults the generator does not see per handler.
/// </remarks>
internal sealed class ErrorContractCheck {
    private readonly string _operation;
    private readonly FrozenDictionary<string, ErrorContract> _declared;

    private ErrorContractCheck(string operation, FrozenDictionary<string, ErrorContract> declared) {
        _operation = operation;
        _declared = declared;
    }

    /// <summary>Builds the check for a route, or <see langword="null"/> when the route declares no contract.</summary>
    public static ErrorContractCheck? Create(string operation, IReadOnlyList<ErrorContract>? errors) {
        return errors is null
            ? null
            : new ErrorContractCheck(
                operation,
                errors.GroupBy(static e => e.Code, StringComparer.Ordinal)
                    .ToFrozenDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal));
    }

    public void Verify(AppError error, IServiceProvider services) {
        if (error.Kind == ErrorKind.Internal) return;

        var code = error.Code;
        if (_declared.TryGetValue(code, out var declared)) {
            if (declared.Kind != error.Kind)
                services.GetService<IErrorContractMonitor>()?.Violation(_operation, error, declared);

            return;
        }

        if (code is ErrorCodes.Unauthorized or ErrorCodes.Forbidden) return;

        services.GetService<IErrorContractMonitor>()?.Violation(_operation, error, null);
    }
}
