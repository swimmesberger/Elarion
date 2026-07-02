using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Elarion.Settings.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class PostgreSqlSettingsChangeRegistrationTests {
    private const string ConnectionString = "Host=localhost;Database=elarion;Username=elarion;Password=elarion";

    private static ServiceCollection CreateServices() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SettingsIntegrationDbContext>(options => options.UseNpgsql(ConnectionString));
        return services;
    }

    [Fact]
    public void ReplacesInProcessChangeSourceAndPublisher() {
        var services = CreateServices();
        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();
        services.AddElarionPostgreSqlSettingsChanges(ConnectionString);

        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<ISettingsChangeSource>();
        source.Should().BeOfType<PostgreSqlSettingsChangeSource>();
        provider.GetRequiredService<ISettingsChangePublisher>().Should().BeSameAs(source);
    }

    [Fact]
    public void IsAuthoritativeRegardlessOfRegistrationOrder() {
        var services = CreateServices();
        services.AddElarionPostgreSqlSettingsChanges(ConnectionString);
        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISettingsChangeSource>().Should().BeOfType<PostgreSqlSettingsChangeSource>();
        provider.GetRequiredService<IEfCoreSettingsChangeNotifier>()
            .Should().BeOfType<PostgreSqlTransactionalSettingsChangeNotifier>();
    }

    [Fact]
    public void RegistersTransactionalNotifierAndListener() {
        var services = CreateServices();
        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();
        services.AddElarionPostgreSqlSettingsChanges(ConnectionString);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEfCoreSettingsChangeNotifier>()
            .Should().BeOfType<PostgreSqlTransactionalSettingsChangeNotifier>();
        provider.GetServices<IHostedService>()
            .Should().ContainSingle(service => service is PostgreSqlSettingsChangeListener);
    }

    [Fact]
    public async Task MultiInstanceWarningDoesNotFireWithPostgreSqlSource() {
        var services = CreateServices();
        var logger = new CapturingLogger();
        services.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(logger));
        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();
        services.AddElarionPostgreSqlSettingsChanges(ConnectionString);

        await using var provider = services.BuildServiceProvider();
        var warning = provider.GetServices<IHostedService>()
            .OfType<MultiInstanceChangeNotificationWarning>()
            .Should().ContainSingle().Subject;

        await warning.StartAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void PayloadRoundTripsScopeAndKey() {
        var payload = PostgreSqlSettingsChangePayload.Serialize(SettingsScope.User("u-1"), "app:theme");

        PostgreSqlSettingsChangePayload.TryDeserialize(payload, out var scope, out var key).Should().BeTrue();
        scope.Should().Be(SettingsScope.User("u-1"));
        key.Should().Be("app:theme");
    }

    [Fact]
    public void MalformedPayloadIsRejected() {
        PostgreSqlSettingsChangePayload.TryDeserialize("not json", out _, out _).Should().BeFalse();
        PostgreSqlSettingsChangePayload.TryDeserialize("{}", out _, out _).Should().BeFalse();
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory {
        public ILogger CreateLogger(string categoryName) => logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
