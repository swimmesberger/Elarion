using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.Hosting;

namespace Elarion.Sql;

/// <summary>
/// Installs the canonical JSON accessor into <see cref="ElarionSqlJson"/> at startup, so self-mapping
/// <see cref="SqlJsonAttribute">[SqlJson]</see> rows serialize their JSON columns through the one
/// canonical configuration (ADR-0023) without the host wiring anything. The generated
/// <c>AddElarionSqlMappers</c> registers this only when the assembly declares a JSON-column mapper, so
/// a JSON-free host pays nothing. DI-free hosts install it directly via <see cref="ElarionSqlJson.Use"/>.
/// </summary>
public sealed class ElarionSqlJsonInstaller(IElarionJsonSerialization serialization) : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) {
        ElarionSqlJson.Use(serialization);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
