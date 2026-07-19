using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class EfCoreSettingsStoreRegistrationTests {
    [Fact]
    public void AddElarionSettingsEntityFrameworkCore_RegistersEfStoreAsSettingsStore() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SettingsIntegrationDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=elarion;Username=elarion;Password=elarion"));

        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        // The EF store replaces the in-process default regardless of registration order.
        store.Should().BeOfType<EfCoreSettingsStore<SettingsIntegrationDbContext>>();
    }

    [Fact]
    public void AddElarionSettingsEntityFrameworkCore_StillRegistersManagerAndChangeSource() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SettingsIntegrationDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=elarion;Username=elarion;Password=elarion"));

        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<ISettingsManager>().Should().NotBeNull();
        scope.ServiceProvider.GetService<ISettingsChangeSource>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddElarionSettingsEntityFrameworkCore_WarnsAboutSingleInstanceNotification() {
        var services = new ServiceCollection();
        var logger = new CapturingLogger();
        services.AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(logger));
        services.AddLogging();
        services.AddDbContext<SettingsIntegrationDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=elarion;Username=elarion;Password=elarion"));

        services.AddElarionSettingsEntityFrameworkCore<SettingsIntegrationDbContext>();

        using var provider = services.BuildServiceProvider();
        var warning = provider.GetServices<IHostedService>()
            .OfType<MultiInstanceChangeNotificationWarning>()
            .Should().ContainSingle().Subject;

        await warning.StartAsync(CancellationToken.None);

        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("single-instance"));
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory {
        public ILogger CreateLogger(string categoryName) {
            return logger;
        }

        public void AddProvider(ILoggerProvider provider) {
        }

        public void Dispose() {
        }
    }
}
