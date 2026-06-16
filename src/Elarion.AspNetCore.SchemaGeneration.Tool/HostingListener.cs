using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elarion.AspNetCore.SchemaGeneration.Tool;

internal sealed class HostingListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable {
    private const string HostingDiagnosticListenerName = "Microsoft.Extensions.Hosting";
    private const string HostBuildingEventName = "HostBuilding";
    private const string HostBuiltEventName = "HostBuilt";

    private readonly IDisposable _allListenersSubscription;
    private readonly List<IDisposable> _subscriptions = [];
    private IHost? _host;

    public HostingListener() {
        _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public IHost GetCapturedHost() =>
        _host ?? throw new InvalidOperationException(
            "The application entry point exited without building a Microsoft.Extensions.Hosting host.");

    public void OnNext(DiagnosticListener value) {
        if (value.Name == HostingDiagnosticListenerName) {
            _subscriptions.Add(value.Subscribe(this));
        }
    }

    public void OnNext(KeyValuePair<string, object?> value) {
        switch (value.Key) {
            case HostBuildingEventName when value.Value is IHostBuilder builder:
                builder.ConfigureServices((_, services) => {
                    services.RemoveAll<IHostLifetime>();
                    services.AddSingleton<IHostLifetime, NoopHostLifetime>();

                    services.RemoveAll<IServer>();
                    services.AddSingleton<IServer, NoopServer>();
                });
                break;
            case HostBuiltEventName when value.Value is IHost host:
                _host = host;
                throw new HostAbortedException();
        }
    }

    public void OnCompleted() {
    }

    public void OnError(Exception error) {
    }

    public void Dispose() {
        foreach (var subscription in _subscriptions) {
            subscription.Dispose();
        }

        _allListenersSubscription.Dispose();
    }
}

internal sealed class HostAbortedException : Exception;
