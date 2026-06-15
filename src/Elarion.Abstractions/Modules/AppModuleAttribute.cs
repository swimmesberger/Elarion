namespace Elarion.Abstractions.Modules;

/// <summary>
/// Marks a class as an application module boundary and application-level entry point.
/// Source generators use this attribute to group handlers and validators, resolve module
/// pipeline defaults, and emit host bootstrapping calls.
/// <para>
/// The annotated class may implement any of these static methods. All methods are optional
/// and convention-based:
/// <list type="bullet">
///   <item>
///     <c>static void ConfigureServices(IServiceCollection services, IConfiguration configuration)</c>
///     — register application services for this module using dependency injection abstractions.
///   </item>
///   <item>
///     <c>static void MapEndpoints(IEndpointRouteBuilder endpoints)</c>
///     — declare Minimal API endpoint mappings for this module.
///   </item>
///   <item>
///     <c>static IJsonTypeInfoResolver GetJsonTypeInfoResolver()</c>
///     — return the module's STJ source-generated serializer context.
///   </item>
/// </list>
/// </para>
/// <para>
/// Feature modules are enabled by default. Disable via configuration:
/// <c>Modules:{Name}:Enabled = false</c>
/// Core modules are always enabled and ignore feature flags.
/// </para>
/// </summary>
/// <example>
/// <code>
/// [AppModule("Chat")]
/// public static class ChatModule {
///     public static void ConfigureServices(IServiceCollection services, IConfiguration config) {
///         services.AddScoped&lt;IConversationService, ConversationService&gt;();
///     }
///
///     public static void MapEndpoints(IEndpointRouteBuilder endpoints) {
///         endpoints.MapPost("/chat/stream", HandleStreamAsync).RequireAuthorization();
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AppModuleAttribute(string name) : Attribute {
    /// <summary>
    /// The unique module name. Used for feature-flag keys (<c>Modules:{Name}:Enabled</c>)
    /// and logging.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Determines whether the module is an optional feature or an always-enabled foundation module.
    /// </summary>
    public AppModuleKind Kind { get; init; } = AppModuleKind.Feature;

    /// <summary>
    /// Optional comma-separated list of module names this module depends on.
    /// The generator topologically sorts modules so dependencies are initialized first.
    /// </summary>
    public string? DependsOn { get; init; }
}
