using Azure.Storage.Blobs;
using Testcontainers.Azurite;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Starts a disposable Azurite (Azure Storage emulator) container for the Azure blob and staged-upload
/// integration tests. When Docker is not available the fixture records a skip reason instead of failing,
/// so the suite still runs (and these tests skip) on machines without Docker.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime {
    private AzuriteContainer? _container;

    /// <summary>Gets a value indicating whether the emulator started.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Gets the reason the integration tests are skipped when <see cref="IsAvailable"/> is false.</summary>
    public string SkipReason { get; private set; } = "";

    /// <summary>Gets a service client bound to the emulator.</summary>
    public BlobServiceClient Client { get; private set; } = null!;

    /// <summary>Gets the emulator connection string, for tests that need a client with custom pipeline options.</summary>
    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync() {
        AzuriteContainer container;
        try {
            // Build() validates the Docker endpoint, so it must run inside the guard too. The SDK's
            // default service version can be newer than what Azurite ships (the SDK releases first), so
            // skip the emulator's version check — the operations exercised here are stable-surface.
            container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0")
                .WithCommand("--skipApiVersionCheck")
                .Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            SkipReason = $"Azurite Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        ConnectionString = container.GetConnectionString();
        Client = new BlobServiceClient(ConnectionString);
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) {
            await _container.DisposeAsync();
        }
    }
}
