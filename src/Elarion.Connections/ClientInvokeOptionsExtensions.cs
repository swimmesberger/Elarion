using Elarion.Abstractions.Connections;

namespace Elarion.Connections;

/// <summary>
/// The adapter-side normalization step behind
/// <see cref="ElarionConnectionsOptions.DefaultInvokeTimeout"/>: a sink resolves the effective invoke
/// options once, before its codec sees them, so the timeout layering (per-call
/// <see cref="ClientInvokeOptions.Timeout"/> &gt; kernel default &gt; unbounded) is identical on every
/// transport. Third-party adapters should apply it in their <c>InvokeAsync</c> exactly like the shipped
/// ones do.
/// </summary>
public static class ClientInvokeOptionsExtensions {
    /// <summary>
    /// Returns options whose <see cref="ClientInvokeOptions.Timeout"/> falls back to
    /// <paramref name="defaultTimeout"/> when the call set none. A per-call timeout always wins —
    /// including an explicit <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>, the per-call escape
    /// to unbounded; a <see langword="null"/> default leaves the options untouched.
    /// </summary>
    /// <param name="options">The caller's per-call options, or <see langword="null"/>.</param>
    /// <param name="defaultTimeout">The adapter's configured default invoke timeout.</param>
    public static ClientInvokeOptions? WithDefaultTimeout(this ClientInvokeOptions? options, TimeSpan? defaultTimeout) {
        if (options?.Timeout is not null || defaultTimeout is null) {
            return options;
        }

        return options is null
            ? new ClientInvokeOptions { Timeout = defaultTimeout }
            : options with { Timeout = defaultTimeout };
    }
}
