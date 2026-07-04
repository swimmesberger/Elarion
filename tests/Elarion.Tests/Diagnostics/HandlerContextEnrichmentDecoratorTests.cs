using System.Diagnostics;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Identity;
using Elarion.Diagnostics;
using Elarion.Identity;
using Elarion.Pipeline;
using Elarion.Tests.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Diagnostics;

public sealed class HandlerContextEnrichmentDecoratorTests {
    [Fact]
    public async Task Authenticated_StampsUserIdRolesPermissions_OnHandlerSpan() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        var user = Authenticated(
            id: "u-1",
            roles: ["admin", "auditor"],
            permissions: ["invoices.read", "invoices.write"]);

        await Trace([UserEnricher(user)]).HandleAsync(new Request(), TestContext.Current.CancellationToken);

        var span = Span(activities);
        span.GetTag("user.id").Should().Be("u-1");
        span.GetTag("user.roles").Should().Be("admin,auditor");
        span.GetTag("user.permissions").Should().Be("invoices.read,invoices.write");
        span.GetTag("user.email").Should().BeNull();
    }

    [Fact]
    public async Task UserIdentity_NeverLeaksToExecutionMetric() {
        using var meters = new MeterCollector(HandlerTelemetry.MeterName);
        var user = Authenticated("u-1", roles: ["admin"], permissions: ["invoices.read"]);

        await Trace([UserEnricher(user)]).HandleAsync(new Request(), TestContext.Current.CancellationToken);

        meters.Measurements.Should().Contain(m => m.InstrumentName == "handler.execution.count");
        meters.Measurements.Should().OnlyContain(m => !m.Tags.ContainsKey("user.id"));
        meters.Measurements.Should().OnlyContain(m => !m.Tags.ContainsKey("user.roles"));
    }

    [Fact]
    public async Task Authenticated_OpensLogScope_WithUserContext() {
        var logger = new RecordingLoggerFactory();
        var user = Authenticated("u-1", roles: ["admin"], permissions: ["invoices.read"]);

        await Build([UserEnricher(user)], logger).HandleAsync(new Request(), TestContext.Current.CancellationToken);

        var items = ToMap(logger.Logger.Scopes.Should().ContainSingle().Subject);
        items.Should().Contain("UserId", "u-1");
        items.Should().Contain("UserRoles", "admin");
        items.Should().Contain("UserPermissions", "invoices.read");
        items.Should().NotContainKey("UserEmail");
    }

    [Fact]
    public async Task NoEnrichers_IsInert_NoScope() {
        var logger = new RecordingLoggerFactory();

        var result = await Build([], logger).HandleAsync(new Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        logger.Logger.Scopes.Should().BeEmpty();
    }

    [Fact]
    public async Task AnonymousCaller_BuiltInContributesNothing() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        var logger = new RecordingLoggerFactory();
        var anonymous = new FakeCurrentUser { IsAuthenticated = false };

        await Trace([UserEnricher(anonymous)], logger)
            .HandleAsync(new Request(), TestContext.Current.CancellationToken);

        Span(activities).GetTag("user.id").Should().BeNull();
        logger.Logger.Scopes.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_BuiltInContributesNothing() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        var user = Authenticated("u-1", roles: ["admin"]);

        await Trace([UserEnricher(user, new UserContextEnrichmentOptions { Enabled = false })])
            .HandleAsync(new Request(), TestContext.Current.CancellationToken);

        Span(activities).GetTag("user.id").Should().BeNull();
    }

    [Fact]
    public async Task IncludeEmail_OptsIn_StampsEmail() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        var logger = new RecordingLoggerFactory();
        var user = Authenticated("u-1", email: "a@b.test");

        await Trace([UserEnricher(user, new UserContextEnrichmentOptions { IncludeEmail = true })], logger)
            .HandleAsync(new Request(), TestContext.Current.CancellationToken);

        Span(activities).GetTag("user.email").Should().Be("a@b.test");
        ToMap(logger.Logger.Scopes.Single()).Should().Contain("UserEmail", "a@b.test");
    }

    [Fact]
    public async Task Roles_AreBounded_ByMaxItems() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        var user = Authenticated("u-1", roles: ["a", "b", "c", "d"]);

        await Trace([UserEnricher(user, new UserContextEnrichmentOptions { MaxItems = 2 })])
            .HandleAsync(new Request(), TestContext.Current.CancellationToken);

        Span(activities).GetTag("user.roles").Should().Be("a,b");
    }

    [Fact]
    public async Task HostEnricher_ComposesWithBuiltIn() {
        using var activities = new ActivityCollector(HandlerTelemetry.ActivitySourceName);
        var logger = new RecordingLoggerFactory();
        var user = Authenticated("u-1");

        await Trace([UserEnricher(user), new TenantEnricher("acme")], logger)
            .HandleAsync(new Request(), TestContext.Current.CancellationToken);

        var span = Span(activities);
        span.GetTag("user.id").Should().Be("u-1");     // built-in
        span.GetTag("tenant.id").Should().Be("acme");  // host contribution
        ToMap(logger.Logger.Scopes.Single()).Should().Contain("TenantId", "acme");
    }

    [Fact]
    public void AddElarionClaimsCurrentUser_RegistersBuiltInUserEnricher_ByDefault() {
        var services = new ServiceCollection();
        services.AddElarionClaimsCurrentUser();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var enrichers = scope.ServiceProvider.GetServices<IHandlerContextEnricher>();

        enrichers.Should().ContainSingle(e => e is UserContextEnricher);
    }

    [Fact]
    public void AddElarionUserContextEnrichment_Disable_Wins_RegardlessOfOrder() {
        var services = new ServiceCollection();
        services.AddElarionUserContextEnrichment(o => o.Enabled = false);
        services.AddElarionClaimsCurrentUser(); // registers the default options after the explicit opt-out

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<UserContextEnrichmentOptions>().Enabled.Should().BeFalse();
    }

    private static IHandler<Request, Result<int>> Build(
        IEnumerable<IHandlerContextEnricher> enrichers, ILoggerFactory? loggerFactory = null) =>
        new HandlerContextEnrichmentDecorator<Request, Result<int>>(new SuccessHandler(), enrichers, loggerFactory);

    // Wrap the enrichment decorator in a real tracing span, mirroring the generated pipeline (tracing outermost),
    // so Activity.Current during enrichment is the handler span the tags land on.
    private static IHandler<Request, Result<int>> Trace(
        IEnumerable<IHandlerContextEnricher> enrichers, ILoggerFactory? loggerFactory = null) =>
        new TracingDecorator<Request, Result<int>>(Build(enrichers, loggerFactory), "H");

    private static UserContextEnricher UserEnricher(ICurrentUser user, UserContextEnrichmentOptions? options = null) =>
        new(user, options ?? new UserContextEnrichmentOptions());

    private static Activity Span(ActivityCollector activities) =>
        activities.Activities.Should().ContainSingle(a => a.DisplayName == "handle H").Subject;

    private static FakeCurrentUser Authenticated(
        string id, string[]? roles = null, string[]? permissions = null, string? email = null) =>
        new() {
            UserId = id,
            IsAuthenticated = true,
            Email = email,
            Roles = roles ?? [],
            ClaimsByType = permissions is null
                ? new Dictionary<string, string[]>()
                : new Dictionary<string, string[]> { ["permission"] = permissions },
        };

    private static IReadOnlyDictionary<string, string> ToMap(
        IReadOnlyList<KeyValuePair<string, object>> scope) =>
        scope.ToDictionary(kv => kv.Key, kv => (string)kv.Value);

    private sealed record Request;

    private sealed class SuccessHandler : IHandler<Request, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(Request request, CancellationToken ct) =>
            ValueTask.FromResult(Result<int>.Success(1));
    }

    private sealed class TenantEnricher(string tenant) : IHandlerContextEnricher {
        public void Enrich(HandlerEnrichmentContext context) {
            context.SetTag("tenant.id", tenant);
            context.AddScopeItem("TenantId", tenant);
        }
    }

    private sealed class FakeCurrentUser : ICurrentUser {
        public string UserId { get; init; } = string.Empty;
        public string? Email { get; init; }
        public IReadOnlyList<string> Roles { get; init; } = [];
        public bool IsAuthenticated { get; init; }
        public IReadOnlyDictionary<string, string[]> ClaimsByType { get; init; } =
            new Dictionary<string, string[]>();

        public bool IsInRole(string role) => Roles.Contains(role);

        public bool HasClaim(string type, string value) =>
            ClaimsByType.TryGetValue(type, out var values) && values.Contains(value);

        public IEnumerable<string> GetClaimValues(string type) =>
            ClaimsByType.TryGetValue(type, out var values) ? values : [];
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory {
        public RecordingLogger Logger { get; } = new();
        public ILogger CreateLogger(string categoryName) => Logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class RecordingLogger : ILogger {
        public List<IReadOnlyList<KeyValuePair<string, object>>> Scopes { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull {
            if (state is IReadOnlyList<KeyValuePair<string, object>> items) {
                Scopes.Add(items);
            }

            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }

        private sealed class NullScope : IDisposable {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
