using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Elarion.Actors.Runtime;

/// <summary>
/// Hosts every activation of one registered actor: key → cell map, virtual activation on first
/// message, retry-on-closed routing, and shutdown fan-out.
/// </summary>
internal sealed class ActorHost<TActor, TKey, TFacade> : IActorHostEntry, IActorMailboxRouter<TActor>
    where TActor : class
    where TKey : notnull
    where TFacade : class {
    private readonly ActorRegistration<TActor, TKey, TFacade> _registration;
    private readonly ActorRuntime _runtime;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<TKey, ActorCell<TActor>> _cells = new();
    private volatile bool _stopping;

    internal ActorHost(ActorRegistration<TActor, TKey, TFacade> registration, ActorRuntime runtime) {
        _registration = registration;
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger("Elarion.Actors." + registration.Name);
    }

    public string Name => _registration.Name;

    public Type FacadeType => typeof(TFacade);

    public object CreateFacade(object key) {
        if (key is not TKey) {
            throw new InvalidOperationException(
                $"Actor '{Name}' is keyed by '{typeof(TKey)}' but was resolved with a '{key.GetType()}' key.");
        }

        return _registration.Facade(new ActorHandle<TActor>(
            this, key, _registration.Options, _runtime.TimeProvider, _runtime.CancellationPool, _registration.Name));
    }

    public async ValueTask EnqueueAsync(object key, ActorWorkItem<TActor> item, CancellationToken cancellationToken) {
        var typedKey = (TKey)key;
        // Single-homing gate (ADR-0048): a SingleHomed actor only runs on the instance holding the
        // home lease. Checked per call (not per activation) so a lease lost mid-activation stops
        // NEW work immediately; in-flight turns finish and any conflicting write is caught by the
        // snapshot ETag + transparent retry.
        if (_registration.Options.SingleHomed && _runtime.HomeLease is { IsHeld: false } lease) {
            throw new ActorNotHomedException(Name, typedKey.ToString() ?? string.Empty, lease.CurrentHolder);
        }

        while (true) {
            if (_stopping) {
                throw new InvalidOperationException(
                    $"The actor system is stopping; call to actor '{Name}' ({typedKey}) rejected.");
            }

            var cell = _cells.GetOrAdd(typedKey, static (k, host) => host.CreateCell(k), this);
            cell.EnsureStarted();
            if (await cell.TryEnqueueAsync(item, cancellationToken).ConfigureAwait(false)) {
                return;
            }

            // The cell closed (idle passivation or activation failure) between lookup and enqueue:
            // drop exactly that cell and retry against a fresh activation.
            _cells.TryRemove(new KeyValuePair<TKey, ActorCell<TActor>>(typedKey, cell));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _stopping = true;
        var cells = _cells.ToArray();
        if (cells.Length == 0) {
            return;
        }

        _logger.LogDebug("Stopping {Count} activation(s) of actor {Actor}.", cells.Length, Name);
        await Task.WhenAll(cells.Select(pair => pair.Value.StopAsync(cancellationToken))).ConfigureAwait(false);
    }

    private ActorCell<TActor> CreateCell(TKey key) {
        // GetOrAdd may invoke this for a losing racer; the cell stays inert (no DI scope, no loop)
        // until EnsureStarted, so a discarded duplicate is plain garbage.
        var stoppingCts = new CancellationTokenSource();
        var context = new ActorContext<TKey>(_registration.Name, key, stoppingCts.Token);
        return new ActorCell<TActor>(
            _registration.Name,
            key.ToString() ?? string.Empty,
            serviceProvider => _registration.Activator(serviceProvider, context),
            _registration.Options,
            _runtime.ScopeFactory,
            _runtime.TimeProvider,
            _logger,
            stoppingCts,
            onClosed: cell => _cells.TryRemove(new KeyValuePair<TKey, ActorCell<TActor>>(key, cell)),
            reEnqueue: item => EnqueueAsync(key, item, CancellationToken.None));
    }
}
