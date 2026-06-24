// .NET Aspire orchestration for the Billing sample: provisions a PostgreSQL container and runs the API
// against it. `dotnet run --project samples/Billing/Billing.AppHost` brings up the database, injects its
// connection string into the API (resource name "billing"), and opens the Aspire dashboard with traces
// and metrics collected over OTLP. Requires a container runtime (Docker/Podman).
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

var billingDb = postgres.AddDatabase("billing");

var api = builder.AddProject<Projects.Billing_Api>("api")
    .WithReference(billingDb)
    .WaitFor(billingDb);

// The Vite + React frontend. Aspire runs `npm run dev` and injects the API URL as VITE_API_URL so the
// generated JSON-RPC client points at the right endpoint. Run `npm install` in web/ once beforehand.
builder.AddViteApp("web", "../web")
    .WithReference(api)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
