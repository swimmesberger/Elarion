using AwesomeAssertions;
using Elarion.Sql;
using Elarion.Sql.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// Registration tests for the PostgreSQL data-source provider — deterministic, no container needed (building
/// an <see cref="NpgsqlDataSource"/> does not open a connection). They prove the central data source is shared:
/// the same <see cref="NpgsqlDataSource"/> instance backs the <see cref="IElarionSqlDataSourceProvider"/> the
/// session opens from, so migrations (which resolve <see cref="NpgsqlDataSource"/>) and the access tier use one
/// source — EF Core's <c>DbContext</c> analogue.
/// </summary>
public sealed class SqlPostgreSqlServiceCollectionExtensionsTests {
    private const string ConnectionString = "Host=localhost;Database=test;Username=test;Password=test";

    [Fact]
    public void AddElarionPostgreSqlDataSource_SharesOneCoreSourceBetweenProviderAndDi() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSqlDataSource(ConnectionString);
        services.AddElarionSqlUnitOfWork();

        using var provider = services.BuildServiceProvider();

        // The one core source migrations resolve, and the source the SQL provider hands out, are the same object.
        var core = provider.GetRequiredService<NpgsqlDataSource>();
        provider.GetRequiredService<IElarionSqlDataSourceProvider>().GetDataSource().Should().BeSameAs(core);
    }

    [Fact]
    public async Task AddElarionPostgreSqlDataSource_RegistersAResolvableSession() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionPostgreSqlDataSource(ConnectionString);
        services.AddElarionSqlUnitOfWork();

        await using var provider = services.BuildServiceProvider();
        // The session is IAsyncDisposable (it owns a connection), so its scope must dispose asynchronously —
        // ASP.NET Core's per-request scope does this automatically.
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<ISqlSession>().Should().NotBeNull();
    }

    [Fact]
    public void AddElarionPostgreSqlDataSource_InstanceOverload_WrapsTheGivenSource() {
        var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var services = new ServiceCollection();
        services.AddElarionPostgreSqlDataSource(dataSource);
        services.AddElarionSqlSession();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<NpgsqlDataSource>().Should().BeSameAs(dataSource);
        provider.GetRequiredService<IElarionSqlDataSourceProvider>().GetDataSource().Should().BeSameAs(dataSource);
    }
}
