using System.Runtime.CompilerServices;

namespace Elarion.Actors.Runtime;

/// <summary>
/// Runtime support for generated facade methods returning <see cref="IAsyncEnumerable{T}"/> (actor
/// streams, ADR-0052). The mailbox call — the <em>attach turn</em> — is deferred until the caller
/// actually enumerates, and runs once per enumeration, so the facade result behaves like any other
/// <see cref="IAsyncEnumerable{T}"/>: nothing happens until you iterate, and each iteration is its own
/// subscription.
/// </summary>
public static class ActorStreams {
    /// <summary>
    /// Wraps a per-enumeration mailbox subscribe call. <paramref name="cancellationToken"/> (the facade
    /// method argument) and the enumerator's own token both cancel: the attach turn while queued, the
    /// stream afterwards.
    /// </summary>
    /// <param name="subscribe">Enqueues the attach turn and returns the actor's subscription.</param>
    /// <param name="cancellationToken">The facade method's cancellation token.</param>
    public static IAsyncEnumerable<T> Defer<T>(
        Func<CancellationToken, ValueTask<IAsyncEnumerable<T>>> subscribe,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(subscribe);
        return new DeferredActorStream<T>(subscribe, cancellationToken);
    }

    /// <summary>
    /// Ties an activation retention (<c>ActorWorkItem.RetainActivation</c>) to a stream's lifetime:
    /// the activation stays safe from <b>idle</b> passivation until the enumeration ends — completes,
    /// fails, or is disposed (the refCount lifetime, ADR-0052). Generated stream work items wrap the
    /// actor method's result with this inside the attach turn. The result must be enumerated (the
    /// facade's <see cref="Defer{T}"/> always does) and the enumerator disposed: the retention has no
    /// finalizer, so an abandoned enumeration pins the activation against idle passivation for the
    /// rest of the process lifetime.
    /// </summary>
    /// <param name="source">The actor's subscription.</param>
    /// <param name="retention">The activation retention to release when the enumeration ends; may be
    /// <see langword="null"/> (nothing to retain — e.g. a hand-driven work item outside a cell).</param>
    public static IAsyncEnumerable<T> RetainWhileEnumerating<T>(IAsyncEnumerable<T> source, IDisposable? retention) {
        ArgumentNullException.ThrowIfNull(source);
        return retention is null ? source : Enumerate(source, retention);

        static async IAsyncEnumerable<T> Enumerate(
            IAsyncEnumerable<T> source, IDisposable retention,
            [EnumeratorCancellation] CancellationToken ct = default) {
            try {
                await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false)) yield return item;
            }
            finally {
                retention.Dispose();
            }
        }
    }

    private sealed class DeferredActorStream<T>(
        Func<CancellationToken, ValueTask<IAsyncEnumerable<T>>> subscribe,
        CancellationToken methodToken) : IAsyncEnumerable<T> {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
            return EnumerateAsync(cancellationToken).GetAsyncEnumerator(CancellationToken.None);
        }

        private async IAsyncEnumerable<T> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken enumeratorToken = default) {
            using var linked = methodToken.CanBeCanceled && enumeratorToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(methodToken, enumeratorToken)
                : null;
            var token = linked?.Token ?? (methodToken.CanBeCanceled ? methodToken : enumeratorToken);
            var subscription = await subscribe(token).ConfigureAwait(false);
            await foreach (var item in subscription.WithCancellation(token).ConfigureAwait(false)) yield return item;
        }
    }
}
