using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion;
using Elarion.AspNetCore;
using Elarion.JsonRpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>Tests for the one-call <c>AddJsonRpc(serializerOptions, registerAll)</c> overload.</summary>
public sealed class JsonRpcServiceExtensionsTests {
    private sealed record PingCommand {
        public string? Value { get; init; }
    }

    private sealed record PingResponse(string Value);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public void AddJsonRpc_WithRegisterAll_RegistersDispatcherAndSerializerOptions() {
        var services = new ServiceCollection();

        services.AddElarionJsonRpc(Options, d => d.MapHandler<PingCommand, PingResponse>("ping"));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<JsonRpcDispatcher>().MethodNames.Should().Contain("ping");
        provider.GetRequiredService<IOptions<JsonRpcOptions>>().Value.SerializerOptions
            .Should().BeSameAs(Options);
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
        services.AddElarionJsonRpc(Options, (dispatcher, config) => {
            if (config.GetValue("Modules:Shipping:Enabled", true))
                dispatcher.MapHandler<PingCommand, PingResponse>("shipments.create");
            return dispatcher;
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<JsonRpcDispatcher>().MethodNames
            .Contains("shipments.create").Should().Be(shouldContain);
    }
}
