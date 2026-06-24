using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Billing.Api.Hosting;
using Billing.Application;
using Billing.Application.Data;
using Billing.Domain;
using Elarion.AspNetCore;
using Elarion.JsonRpc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

// In-memory EF Core so the sample runs with no external database. The concrete context is the only
// place a provider is chosen; handlers see only IAppDbContext.
builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("billing-sample"));
builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<AppDbContext>());

// Compose every module's services — handlers, services, validators — each gated by Modules:{Name}:Enabled.
// There are no hand-written Add{Module}…() calls.
builder.Services.AddElarion(builder.Configuration);

// One serializer for runtime dispatch and schema export, built from each module's JSON resolver.
var resolvers = builder.Configuration.GetAllJsonTypeInfoResolvers();
var serializerOptions = new JsonSerializerOptions {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    TypeInfoResolver = JsonTypeInfoResolver.Combine(
        [JsonRpcJsonContext.Default, .. resolvers, new DefaultJsonTypeInfoResolver()]),
};
builder.Services.AddSingleton(serializerOptions);
builder.Services.AddJsonRpc(serializerOptions, ModuleBootstrapper.RegisterRpcMethods);

var app = builder.Build();

// Seed one client so clients.get returns data.
using (var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Clients.Add(new Client {
        Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Name = "Acme Inc.",
        Email = "billing@acme.example",
        CreatedAt = TimeProvider.System.GetUtcNow(),
    });
    db.SaveChanges();
}

app.MapElarion(app.Configuration);
app.MapJsonRpc();

app.Run();
