using System.Security.Claims;
using Billing.Api;
using Billing.Application;
using Billing.Application.Modules.Invoicing.Services;
using Billing.Application.Persistence;
using Billing.Infrastructure.Email;
using Elarion.Abstractions.Diagnostics;
using Elarion.Diagnostics;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Scheduling;
using Elarion.AspNetCore;
using Elarion.AspNetCore.Identity;
using Elarion.Session;
using Elarion.AspNetCore.Mcp;
using Elarion.Actors.PostgreSql;
using Elarion.AspNetCore.OpenApi;
using Elarion.Auditing.EntityFrameworkCore;
using Elarion.Authorization;
using Elarion.Caching;
using Elarion.Caching.PostgreSql;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Elarion.JsonRpc;
using Elarion.Messaging.Outbox;
using Elarion.Resilience;
using Elarion.Scheduling;
using Elarion.Validation;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
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

// Framework audit trail (ADR-0045): the durable EF sink over the billing context. [Auditable] handlers
// (CreateClient, CreateInvoice) then record one compliance AuditRecord per invocation — committed atomically
// with the transaction, denied attempts included — and [Audited] entities add automatic field-level change
// capture. Retention is off by default.
builder.Services.AddElarionAuditingEntityFrameworkCore<BillingDbContext>();

// Actor state snapshotting (ADR-0047): the dunning actor's IActorState<ClientDunningState> persists as a
// jsonb row per client on the billing database, so the escalation latch survives passivation and restarts.
builder.Services.AddElarionPostgreSqlActorSnapshots<BillingDbContext>();

// Infrastructure capability: the concrete email sender behind the module's port. (The account-standing
// credit policy is a Core [ModuleContract] with a Core-internal [Service] impl — so it self-registers.)
builder.Services.AddScoped<IInvoiceEmailSender, SmtpInvoiceEmailSender>();

// Scheduler runtime. Job descriptors and event consumers are composed per module by
// AddElarion below — there is no explicit Add…ScheduledJobs call.
builder.Services.AddElarionScheduler(builder.Configuration);

// Resilience: generated policy metadata + the Microsoft/Polly-backed runtime.
builder.Services.AddBilling_ApplicationResiliencePolicies();
builder.Services.AddElarionResilience();

// Per-user handler caching, backed by HybridCache with a PostgreSQL L2 — the recommended L2 for most apps
// already on Postgres: it reuses the "billing" database (an auto-created UNLOGGED cache table) instead of
// operating a separate Redis. HybridCache's in-process L1 still carries the hot path. During build-time schema
// generation the Aspire-injected connection string is absent, so fall back to the in-memory tier — the schema
// tool only builds the host, it never serves traffic.
if (JsonRpcSchemaGeneration.IsRunning)
    builder.Services.AddElarionHandlerCaching();
else
    builder.Services.AddElarionPostgreSqlHandlerCaching(builder.Configuration.GetConnectionString("billing")!);

// Transport-neutral current user, filled from the authenticated principal.
builder.Services.AddElarionCurrentUser(options => options.UserIdClaimType = "sub");

// Declarative authorization: the [RequirePermission]/[RequireRole]/… attributes on handlers are enforced by
// a generated decorator before the handler runs — the same check under JSON-RPC, MCP, and HTTP, evaluated
// against ICurrentUser's claims with no HttpContext dependency. Registers the default ClaimsAuthorizer.
builder.Services.AddElarionAuthorization();

// Declarative request validation (ADR-0027): the DataAnnotations on handler request DTOs are enforced by a
// generated, auto-attached decorator before a request reaches caching, the pipeline, or the transaction —
// the same constraints exported to rpc-schema.json, the OpenAPI document, and the generated Zod client.
builder.Services.AddElarionValidation();

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

// Client-capability bootstrap (ADR-0030): a framework-shipped handler that returns module enablement, the
// [ClientFeatures] flags/variants, and the user's grants for the frontend. The manifest is built once from the
// generated bootstrapper; the handler evaluates the flags per user.
builder.Services.AddElarionSession(builder.Configuration.GetClientCapabilityManifest());

// Align ASP.NET's minimal-API JSON options with Elarion's canonical serialization, so [HttpEndpoint] request
// bodies deserialize through the same source-generated contexts every transport uses (needed with reflection
// off / AOT). Responses already bypass these options via ElarionHttpResults. A later ConfigureHttpJsonOptions
// would override this. AddElarionOpenApi below also ensures it, so this call is optional when OpenAPI is used.
builder.Services.AddElarionHttpJson();

// The session bootstrap is framework-owned (no [Handler] attribute), so it is mapped onto the named bus
// imperatively (ADR-0031) — chained into the same RegisterHandlers delegate passed to every transport, so the
// shared dispatcher is still built once.
var registerHandlers = (HandlerDispatcher dispatcher, IConfiguration configuration) =>
    ElarionBootstrapper.RegisterHandlers(dispatcher, configuration).MapElarionSession();

// JSON-RPC: methods gated per module. Serialization comes from the canonical IElarionJsonSerialization
// (module contexts from AddElarion + the JSON-RPC envelope context); customize it via ConfigureElarionJson.
builder.Services.AddElarionJsonRpc(registerHandlers);

// MCP: an equally gated transport adapter over the same shared handler registry (the named bus).
builder.Services.AddElarionMcp(
    builder.Configuration.GetMcpMetadata(),
    registerHandlers,
    o => o.ServerName = "Billing");

// OpenAPI for the REST transport: brings the [HttpEndpoint] handlers to schema/contract parity with JSON-RPC.
// Wires the canonical Elarion JSON into schema generation, keeps the generator's module tags, normalizes
// operation ids, and advertises the Idempotency-Key contract. Serve it with app.MapOpenApi() below.
builder.Services.AddElarionOpenApi();

// Telemetry: register the Elarion sources/meters plus the database signals (Npgsql command spans,
// Npgsql + EF Core meters) and export structured logs, so the Aspire dashboard shows the whole story
// over OTLP: HTTP → handler span → EF/Npgsql span, metrics, and per-request log scopes.
builder.Logging.AddOpenTelemetry(logging => {
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.AddOtlpExporter();
});
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(
            JsonRpcTelemetry.ActivitySourceName,
            SchedulerTelemetry.ActivitySourceName,
            HandlerCacheTelemetry.ActivitySourceName,
            ResilienceTelemetry.ActivitySourceName,
            HandlerTelemetry.ActivitySourceName)
        .AddSource("Npgsql")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddMeter(
            JsonRpcTelemetry.MeterName,
            SchedulerTelemetry.MeterName,
            HandlerCacheTelemetry.MeterName,
            ResilienceTelemetry.MeterName,
            HandlerTelemetry.MeterName)
        .AddMeter("Npgsql", "Microsoft.EntityFrameworkCore")
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

// Create the schema from the model on startup. This sample is a demo against a fresh Aspire-provisioned
// database, so it uses EnsureCreated rather than EF migrations — no migration files to maintain. A real
// application keeps migrations (db.Database.MigrateAsync()) for versioned, incremental schema changes.
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseCors(DevCorsPolicy);
app.UseAuthentication();

// Development-only: stamp a stable dev principal so ICurrentUser resolves without an external issuer. It
// carries the permission claims the handlers require ([RequirePermission("clients", Verbs.Read)], …) so the
// authorization checks pass locally; a real issuer would mint these from the user's roles/scopes.
if (app.Environment.IsDevelopment())
    app.Use(async (context, next) => {
        if (context.User.Identity?.IsAuthenticated != true)
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new Claim("sub", "dev-user"),
                        new Claim("permission", "clients.read"),
                        new Claim("permission", "clients.write"),
                        new Claim("permission", "invoices.read"),
                        new Claim("permission", "invoices.write")
                    ],
                    "Development"));
        await next();
    });

app.UseElarionCurrentUser(); // snapshot claims into the scoped ICurrentUser
app.UseAuthorization();

app.MapElarionEndpoints(app.Configuration); // generated [HttpEndpoint] REST routes (e.g. GET /clients/{id})
app.MapElarionSession(); // GET /session — client-capability snapshot (anonymous-friendly)
app.MapOpenApi(); // GET /openapi/v1.json — the REST contract (dev-only in production)
app.MapElarionJsonRpc().RequireAuthorization(); // POST /rpc
app.MapElarionMcp().RequireAuthorization(); // /mcp — independent of /rpc

app.Run();
