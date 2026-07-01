using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore;
using Elarion.JsonRpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>Tests for the one-call <c>AddElarionJsonRpc(registerAll)</c> overload.</summary>
public sealed class JsonRpcServiceExtensionsTests {
    private sealed record PingCommand {
        public string? Value { get; init; }
    }

    private sealed record PingResponse(string Value);

    [Fact]
    public void AddJsonRpc_WithRegisterAll_RegistersDispatcherReadingCanonicalOptions() {
        var services = new ServiceCollection();

        services.AddElarionJsonRpc(d => d.Map<PingCommand, PingResponse>("ping"));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<JsonRpcDispatcher>().MethodNames.Should().Contain("ping");
        provider.GetRequiredService<JsonRpcDispatcher>().JsonOptions
            .Should().BeSameAs(provider.GetRequiredService<IElarionJsonSerialization>().Options);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void AddJsonRpc_WithConfigAwareRegister_GatesMethodsByConfiguration(string enabled, bool shouldContain) {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Shipping:Enabled"] = enabled })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddElarionJsonRpc((dispatcher, config) => {
            if (config.GetValue("Modules:Shipping:Enabled", true))
                dispatcher.Map<PingCommand, PingResponse>("shipments.create");
            return dispatcher;
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<JsonRpcDispatcher>().MethodNames
            .Contains("shipments.create").Should().Be(shouldContain);
    }
}
