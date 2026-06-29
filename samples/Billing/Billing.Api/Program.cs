using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Billing.Api.Hosting;
using Billing.Application;
using Billing.Application.Modules.Invoicing.Services;
using Billing.Application.Persistence;
using Billing.Infrastructure.Email;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Scheduling;
using Elarion.AspNetCore;
using Elarion.AspNetCore.Identity;
using Elarion.AspNetCore.Mcp;
using Elarion.Authorization;
using Elarion.Caching;
using Elarion.JsonRpc;
using Elarion.Messaging.Outbox;
using Elarion.Resilience;
using Elarion.Scheduling;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateSlimBuilder(args);

// Clock — used by handlers, jobs, and the current-user snapshot.
builder.Services.AddSingleton(TimeProvider.System);

// Database: the context lives in the application's persistence layer (the database is application logic);
// handlers inject the concrete BillingDbContext directly — there is no IAppDbContext abstraction. Provider
// *registration* is the host's job — the connection string is injected by the Aspire AppHost ("billing").
builder.Services.AddDbContext<BillingDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("billing")));
// The transaction decorator depends on the base DbContext, so expose BillingDbContext under it too.
builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<BillingDbContext>());

// Integration events: durable, after-commit delivery via the EF Core outbox on the billing context.
builder.Services.AddElarionOutbox<BillingDbContext>();

// Infrastructure capability: the concrete email sender behind the module's port. (The audit trail is a
// Core module capability — a [ModuleContract] with a Core-internal [Service] impl — so it self-registers.)
builder.Services.AddScoped<IInvoiceEmailSender, SmtpInvoiceEmailSender>();

// Scheduler runtime. Job descriptors and event consumers are composed per module by
// AddElarion below — there is no explicit Add…ScheduledJobs call.
builder.Services.AddInMemoryScheduler(builder.Configuration);

// Resilience: generated policy metadata + the Microsoft/Polly-backed runtime.
builder.Services.AddBilling_ApplicationResiliencePolicies();
builder.Services.AddMicrosoftResilienceRuntime();

// Per-user handler caching, backed by HybridCache.
builder.Services.AddElarionHandlerCaching();

// Transport-neutral current user, filled from the authenticated principal.
builder.Services.AddElarionCurrentUser(options => options.UserIdClaimType = "sub");

// Declarative authorization: the [RequirePermission]/[RequireRole]/… attributes on handlers are enforced by
// a generated decorator before the handler runs — the same check under JSON-RPC, MCP, and HTTP, evaluated
// against ICurrentUser's claims with no HttpContext dependency. Registers the default ClaimsAuthorizer.
builder.Services.AddElarionAuthorization();

// Authentication: a JWT bearer issuer of your choice (Entra, Auth0, Keycloak, …). Locally, the
// Development-only middleware below stamps a dev principal so the sample runs without an issuer.
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer(options => {
        options.Authority = builder.Configuration["Auth:Authority"];
        options.Audience = builder.Configuration["Auth:Audience"];
        options.RequireHttpsMetadata = false;
    });
builder.Services.AddAuthorization();

// CORS so the Vite dev server (a different origin) can call /rpc and /mcp from the browser.
const string DevCorsPolicy = "dev-frontend";
builder.Services.AddCors(o => o.AddPolicy(DevCorsPolicy, p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Compose every module's services — handlers, services, validators, scheduled jobs, and event
// consumers — each gated by Modules:{Name}:Enabled.
builder.Services.AddElarion(builder.Configuration);

// JSON-RPC: one serializer for runtime dispatch and schema export; methods gated per module.
var serializerOptions = CreateSerializerOptions(builder.Configuration);
builder.Services.AddSingleton(serializerOptions);
builder.Services.AddElarionJsonRpc(serializerOptions, ModuleBootstrapper.RegisterHandlers);

// MCP: an equally gated transport adapter over the same shared handler registry (the named bus).
builder.Services.AddElarionMcp(
    ModuleBootstrapper.GetMcpMetadata(builder.Configuration),
    serializerOptions,
    ModuleBootstrapper.RegisterHandlers,
    o => o.ServerName = "Billing");

// Telemetry: register the Elarion sources/meters; the Aspire dashboard collects them over OTLP.
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(
            JsonRpcTelemetry.ActivitySourceName,
            SchedulerTelemetry.ActivitySourceName,
            HandlerCacheTelemetry.ActivitySourceName,
            ResilienceTelemetry.ActivitySourceName,
            HandlerTelemetry.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddMeter(
            JsonRpcTelemetry.MeterName,
            SchedulerTelemetry.MeterName,
            HandlerCacheTelemetry.MeterName,
            ResilienceTelemetry.MeterName,
            HandlerTelemetry.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

// Apply migrations on startup so the sample runs against a fresh Aspire-provisioned database.
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors(DevCorsPolicy);
app.UseAuthentication();

// Development-only: stamp a stable dev principal so ICurrentUser resolves without an external issuer. It
// carries the permission claims the handlers require ([RequirePermission("clients", Verbs.Read)], …) so the
// authorization checks pass locally; a real issuer would mint these from the user's roles/scopes.
if (app.Environment.IsDevelopment()) {
    app.Use(async (context, next) => {
        if (context.User.Identity?.IsAuthenticated != true) {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new Claim("sub", "dev-user"),
                        new Claim("permission", "clients.read"),
                        new Claim("permission", "clients.write"),
                        new Claim("permission", "invoices.read"),
                        new Claim("permission", "invoices.write"),
                    ],
                    "Development"));
        }
        await next();
    });
}

app.UseElarionCurrentUser();   // snapshot claims into the scoped ICurrentUser
app.UseAuthorization();

app.MapElarionEndpoints(app.Configuration);
app.MapElarionJsonRpc().RequireAuthorization();        // POST /rpc
app.MapElarionMcp().RequireAuthorization();     // /mcp — independent of /rpc

app.Run();

static JsonSerializerOptions CreateSerializerOptions(IConfiguration configuration) {
    var moduleResolvers = configuration.GetAllJsonTypeInfoResolvers();
    return new JsonSerializerOptions {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            [JsonRpcJsonContext.Default, .. moduleResolvers, new DefaultJsonTypeInfoResolver()]),
    };
}
