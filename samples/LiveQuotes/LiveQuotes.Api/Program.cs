using System.Security.Claims;
using Elarion.Actors;
using Elarion.AspNetCore;
using Elarion.AspNetCore.Identity;
using Elarion.AspNetCore.Streams;
using Elarion.ClientEvents.AspNetCore;
using LiveQuotes.Api;
using LiveQuotes.Api.Modules.Market;
using LiveQuotes.Api.Modules.Market.Actors;

// LiveQuotes: the Elarion realtime middle ground. A simulated market feed pumps ~100 ticks/s through
// single-homed in-memory actors; each actor conflates its stream and pushes updates to browsers over
// SSE. The hot path — feed → actor → client event → browser — touches no database, no broker, no cache:
// one process you can read in an afternoon and run with `dotnet run`.

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

// Transport-neutral current user, filled from the authenticated principal. Client-event subscriptions
// are fail-closed and require an authenticated user, so even this open demo carries a principal.
builder.Services.AddElarionCurrentUser(options => options.UserIdClaimType = "sub");

// Resource-scoped subscriptions ({topic, resource: symbol}) are fail-closed by default, but the
// QuoteChanged contract declares [AllowAnyResource] — a symbol is a routing key, not an entitlement —
// so no IClientEventSubscriptionAuthorizer is registered. That seam stays for per-resource entitlements.

// Multi-instance readiness (ADR-0050): advertise this instance's address so a role lease can publish
// it. Costs nothing here — nothing consumes it until a lease is registered.
builder.Services.AddElarionInstanceAddress();

// Compose the Market module: its actors, the generated market.quoteChanged topic, the feed hosted
// service, and the /quotes handlers — all gated by Modules:Market:Enabled. On a multi-node deployment
// this switch IS the placement: enable the module on the worker, disable it on web nodes, and route
// /quotes/* + /events to the worker at the ingress.
builder.Services.AddElarion(builder.Configuration);

// Mirror canonical JSON onto minimal-API binding (reflection-free route/body binding for [HttpEndpoint]).
builder.Services.AddElarionHttpJson();

var app = builder.Build();

// Demo principal: every request runs as a fixed authenticated user so the sample needs no issuer.
// Replace with your real authentication (JWT bearer, Identity, …) — see samples/Billing for both.
app.Use(async (context, next) => {
    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "demo-user")], "Demo"));
    await next();
});
app.UseElarionCurrentUser();   // snapshot claims into the scoped ICurrentUser

// The role-holder proxy (ADR-0050): in this single-process sample it installs NOTHING (no role
// lease is registered), so the pipeline is untouched. The moment you scale to a homogeneous fleet —
// add Elarion.Coordination.PostgreSql + AddElarionPostgreSqlActorHome<AppDbContext>() — every
// instance serves these prefixes by transparently forwarding to the actor home. Inefficient by one
// hop and deliberately so: the prefix list below IS the ingress rule you'll eventually write; move
// it to your load balancer and delete this line.
app.UseElarionRoleHolderProxy("actors", "/quotes", "/events");

app.UseDefaultFiles();
app.UseStaticFiles();          // the demo dashboard (wwwroot/index.html)

app.MapElarionEndpoints(app.Configuration);   // generated [HttpEndpoint] routes: GET /quotes, GET /quotes/{symbol}
app.MapElarionClientEvents("/events");        // the SSE stream: ?subscriptions=[{"topic":"market.quoteChanged","resource":"ELN"}]

// The ordered tier (ADR-0052) next to the conflated hints above: every accepted tick for one symbol,
// in order, over one SSE connection — `id:` is the stream sequence, so the browser's automatic
// Last-Event-ID reconnect resumes gap-free within the actor's replay ring. Try:
//   curl -N http://localhost:5210/quotes/ELN/stream
// Lives under /quotes, so the proxy / ingress rule above already routes it to the actor home.
app.MapElarionStream<Quote>("/quotes/{symbol}/stream", (context, after) =>
    context.Request.RouteValues["symbol"] is string symbol && symbol.Length > 0
        ? context.RequestServices.GetRequiredService<IActorSystem>()
            .Get<IStockQuote>(symbol.ToUpperInvariant()).Watch(after)
        : null);

app.Run();
