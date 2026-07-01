using System.Security.Claims;
using Billing.Api;
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
using Elarion.Caching.PostgreSql;
using Elarion.EntityFrameworkCore.UnitOfWork;
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
// The framework transaction decorator commits over the EF Core unit of work on the billing context; features
// like idempotency compose on the same boundary.
builder.Services.AddElarionUnitOfWork<BillingDbContext>();

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

// Per-user handler caching, backed by HybridCache with a PostgreSQL L2 — the recommended L2 for most apps
// already on Postgres: it reuses the "billing" database (an auto-created UNLOGGED cache table) instead of
// operating a separate Redis. HybridCache's in-process L1 still carries the hot path.
builder.Services.AddElarionPostgreSqlHandlerCaching(builder.Configuration.GetConnectionString("billing")!);

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
// consumers — each gated by Modules:{Name}:Enabled. This also contributes every enabled module's
// source-generated JSON context to the canonical serializer options every subsystem reads.
builder.Services.AddElarion(builder.Configuration);

// JSON-RPC: methods gated per module. Serialization comes from the canonical IElarionJsonSerialization
// (module contexts from AddElarion + the JSON-RPC envelope context); customize it via ConfigureElarionJson.
builder.Services.AddElarionJsonRpc(ElarionBootstrapper.RegisterHandlers);

// MCP: an equally gated transport adapter over the same shared handler registry (the named bus).
builder.Services.AddElarionMcp(
    builder.Configuration.GetMcpMetadata(),
    ElarionBootstrapper.RegisterHandlers,
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
