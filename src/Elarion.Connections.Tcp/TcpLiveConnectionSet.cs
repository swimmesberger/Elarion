using System.Collections.Concurrent;

namespace Elarion.Connections.Tcp;

/// <summary>
/// An endpoint's live connection handles. Runners register their lifetime before their first await and
/// remove it as they complete, so endpoint shutdown can request graceful close everywhere, wait its grace
/// period, force-abort the stragglers' raw transports, and then await every runner — never abandoning a
/// connection task.
/// </summary>
internal sealed class TcpLiveConnectionSet {
    private readonly ConcurrentDictionary<TcpConnectionLifetime, byte> _live = new();

    public void Add(TcpConnectionLifetime lifetime) {
        _live[lifetime] = 0;
    }

    public void Remove(TcpConnectionLifetime lifetime) {
        _live.TryRemove(lifetime, out _);
    }

    /// <summary>Requests a clean close on every live connection: sends drain, teardown runs in order.</summary>
    public void RequestGracefulCloseAll() {
        foreach (var lifetime in _live.Keys) lifetime.RequestGracefulClose(null);
    }

    /// <summary>Force-aborts every remaining connection's raw transport so blocked I/O fails immediately.</summary>
    public void AbortAll() {
        foreach (var lifetime in _live.Keys) lifetime.Abort(null);
    }
}
