using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion;

/// <summary>Creates a dispatch scope for a request-driven stream and keeps it alive for enumeration.</summary>
public static class StreamHandlerInvoker {
    /// <summary>
    /// Starts a decorated stream handler in a fresh seeded scope. A rejected startup disposes the scope before
    /// returning; an accepted invocation owns it until enumeration ends or <see cref="StreamHandlerInvocation{TItem}.DisposeAsync"/> is called.
    /// </summary>
    public static async ValueTask<Result<StreamHandlerInvocation<TItem>>> InvokeAsync<TRequest, TItem>(
        IServiceProvider rootProvider,
        TRequest request,
        DispatchScopeContext? context = null,
        CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(rootProvider);

        var scope = new DispatchScopeLease(rootProvider.CreateDispatchScope(context));
        try {
            var handler = scope.ServiceProvider.GetRequiredService<IStreamHandler<TRequest, TItem>>();
            var start = await handler.HandleAsync(request, ct).ConfigureAwait(false);
            if (!start.IsSuccess) {
                await scope.DisposeAsync().ConfigureAwait(false);
                return start.Error!;
            }

            return new StreamHandlerInvocation<TItem>(start.Value!, scope);
        } catch {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// An accepted stream plus the dispatch scope that owns its scoped services. Enumerating to any terminal state
/// disposes the scope exactly once; callers that never enumerate must dispose this lease explicitly.
/// </summary>
public sealed class StreamHandlerInvocation<TItem> : IAsyncEnumerable<TItem>, IAsyncDisposable {
    private readonly IAsyncEnumerable<TItem> _stream;
    private readonly DispatchScopeLease _scope;
    private readonly object _gate = new();
    private int _state; // 0 = available, 1 = enumerating, 2 = disposal requested
    private Task? _disposeTask;
    private TaskCompletionSource? _enumerationCompletion;

    internal StreamHandlerInvocation(IAsyncEnumerable<TItem> stream, DispatchScopeLease scope) {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _scope = scope;
    }

    /// <inheritdoc />
    public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
        IAsyncEnumerator<TItem> inner;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_state == 2, this);
            if (_state != 0) throw new InvalidOperationException("A stream handler invocation can only be enumerated once.");
            _state = 1;
            try {
                inner = _stream.GetAsyncEnumerator(cancellationToken);
            } catch {
                _state = 2;
                _disposeTask = _scope.DisposeAsync().AsTask();
                throw;
            }
        }
        return new Enumerator(this, inner);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        Task disposal;
        lock (_gate) {
            if (_state == 0) {
                _state = 2;
                (_stream as IStreamInvocationObservation)?.Abandon();
                disposal = _disposeTask ??= _scope.DisposeAsync().AsTask();
            } else if (_state == 1) {
                _state = 2;
                disposal = (_enumerationCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            } else if (_disposeTask is not null) {
                disposal = _disposeTask;
            } else {
                disposal = (_enumerationCompletion ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
        await disposal.ConfigureAwait(false);
    }

    private async ValueTask CompleteEnumerationAsync() {
        Task disposal;
        TaskCompletionSource? completion;
        lock (_gate) {
            _state = 2;
            disposal = _disposeTask ??= _scope.DisposeAsync().AsTask();
            completion = _enumerationCompletion;
        }

        try {
            await disposal.ConfigureAwait(false);
            completion?.TrySetResult();
        } catch (Exception exception) {
            completion?.TrySetException(exception);
            throw;
        }
    }

    private sealed class Enumerator(StreamHandlerInvocation<TItem> owner, IAsyncEnumerator<TItem> inner) : IAsyncEnumerator<TItem> {
        private int _completed;

        public TItem Current => inner.Current;

        public async ValueTask<bool> MoveNextAsync() {
            try {
                var moved = await inner.MoveNextAsync().ConfigureAwait(false);
                if (!moved)
                    await FinishAsync().ConfigureAwait(false);
                return moved;
            } catch {
                await FinishAsync().ConfigureAwait(false);
                throw;
            }
        }

        public ValueTask DisposeAsync() => FinishAsync();

        // The iterator may use scoped services in its finally block. Its cleanup therefore has to finish before
        // releasing the dispatch scope, regardless of whether enumeration ended normally, faulted, or cancelled.
        // This also makes an external invocation.DisposeAsync wait for one exact terminal cleanup path.
        private async ValueTask FinishAsync() {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            try {
                await inner.DisposeAsync().ConfigureAwait(false);
            } finally {
                await owner.CompleteEnumerationAsync().ConfigureAwait(false);
            }
        }
    }
}

internal sealed class DispatchScopeLease(AsyncServiceScope scope) : IAsyncDisposable {
    private readonly object _gate = new();
    private Task? _disposeTask;

    public IServiceProvider ServiceProvider => scope.ServiceProvider;

    public ValueTask DisposeAsync() {
        Task disposal;
        lock (_gate)
            disposal = _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync() =>
        await scope.DisposeAsync().ConfigureAwait(false);
}
