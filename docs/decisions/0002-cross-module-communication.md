# ADR-0002: Direct cross-module communication

- Status: Accepted
- Date: 2026-06-23
- Related: [ADR-0001](0001-event-transaction-phase.md) (the event planes),
  [module default services](../../AGENTS.md#module-default-services),
  [decorator pipelines](../concepts/decorator-pipelines.mdx)

## Context

Elarion positions itself as a modular-monolith alternative to microservices. For
*asynchronous* cross-module communication it already has the two event planes from
ADR-0001 (`IDomainEventBus`, `IIntegrationEventBus`). What was missing is a sanctioned
answer for **direct, synchronous module-to-module calls** — the in-process analog of a
gRPC call between services.

Three candidate mechanisms were on the table:

1. **Reference another module's handler / `[Service]` directly.** Works, but couples the
   consumer to the *implementation*, turns every internal type into a de-facto public API,
   and erodes the module boundary.
2. **`IDomainEventBus.RequestAsync`.** A mediator-style in-process request/response. It
   decouples the *type*, but the consumer still references the request/response DTOs, dispatch
   is dynamic (a missing responder is a runtime error), and — decisively — ADR-0001 declares
   Plane A "in-process by nature, never broker messages." So `RequestAsync` is *not* an
   extraction path toward a real service boundary.
3. **A published contract interface.** The consumer depends on an intentional, stable
   interface; the implementation stays internal to the owning module. This is what gRPC
   actually is: the client references a contract, never the server's internals.

Two facts about the existing framework shaped the decision:

- **The decorated pipeline *is* the resolved `IHandler<,>`.** `HandlerRegistrationGenerator`
  registers the cache → decorators → resilience → tracing chain *as*
  `IHandler<TRequest, TResponse>`. So invoking a handler in-process is a one-liner that already
  runs the full pipeline — there is no "raw vs decorated" trap for a hand-written adapter.
- **`[Service]` already gives gated, module-scoped DI registration.** A hand-written adapter
  marked `[Service]` is registered and feature-gated through `ConfigureDefaultServices` like
  everything else.

Together these mean a hand-written contract adapter is genuinely trivial (inject the
`IHandler<,>`, map, forward), so generating that adapter would add almost nothing. The
framework's leverage is elsewhere.

## Decision

**The sanctioned mechanism for direct cross-module communication is a published contract
interface.** A module exposes an interface marked `[ModuleContract]` and keeps the
implementation internal; other modules inject the contract. Mapping between the contract's
DTOs and a module's handler DTOs is the **module's concern**, written by hand or with any
mapper (Mapperly, AutoMapper, …) — the framework owns no mapper and emits no forwarder for it.

The framework owns the two things a consumer *cannot* cheaply build and that actually enforce
the architecture, plus one optional ergonomic layer:

### 1. The convention — `[ModuleContract]` (owned)

A marker on the interface (or class) that says "this is a module's published, cross-module
surface." It documents intent and is the anchor the boundary analyzer keys off.

### 2. The boundary analyzer — `ELMOD002` (owned)

A `DiagnosticAnalyzer` (the repo's first) with a purely **location-based** rule: everything under an
`[AppModule]` is module-internal and may not be referenced from another module except through a published
`[ModuleContract]`; everything **outside** every module is shareable. So it reports a cross-module
dependency on a type *inside* another module — an entity, DTO, `[Service]`, handler, or
`[EntityConfiguration]` — and allows a `[ModuleContract]`. To stay precise and low-noise it inspects each
type's **dependency surface** (constructor parameters, fields, properties) — where foreign internals leak
in via DI — not every reference. Framework and shared-kernel types are exempt automatically: a type whose
namespace is under no `[AppModule]` has no owning module. A shared-kernel entity is therefore exempt
because of *where it lives* (outside every module), **not** because entities are special — placing an
entity inside a module makes it module-owned and flagged, which is how a module earns data ownership on the
way to a bounded context (see [ADR-0008](0008-bounded-contexts-and-the-graduation-path.md)). The rule
reads only the module name, so foundation (`Core`) modules get no exemption either. A flagged reference is
resolved one of three ways — a `[ModuleContract]` (a genuine, sparingly-used cross-module *domain* call), a
platform-capability port outside the modules (the port/adapter pattern), or moving shared data to the
shared kernel. Severity is `Warning` (configurable by the host).

### 3. The typed in-process API — `[GenerateModuleApi]` (optional ergonomic layer)

A generated, typed facade over a module's own handlers, so a contract implementation (or any
intra-module code) can call handlers by name instead of resolving verbose generic
`IHandler<,>` types. **It is not a transport** — it crosses no serialization boundary, dispatches
typed-direct to `IHandler<,>` (full pipeline), and is absent from the JSON-RPC/MCP schema.
Because its methods expose handler DTOs, it is module-internal and must not cross a boundary.

Membership mirrors `[EntityConfiguration]`/`[GenerateDbSets]` exactly — the same scope vocabulary the
codebase already uses — with one principled inversion of the default:

| Concern | DbContext grouping | Module API |
|---|---|---|
| Container declared by the user | `[GenerateDbSets(scopes)]` partial interface | `[GenerateModuleApi(scopes)]` partial interface |
| Member tagging | `[EntityConfiguration(scopes)]` | `[ModuleApi(scopes)]` |
| Default membership | an entity participates by having an `[EntityConfiguration]` (the EF side opts out of a DbSet only by omitting a configuration) | **opt-out** (every handler is in) |

The default is opt-out because **handler-ness is structural** (the type implements `IHandler<,>`,
already discovered), so a handler being in its module's facade by default matches its inherent
in-process callability. Entity-ness is now expressed by an `[EntityConfiguration]` — the
configuration is the marked, structural anchor that gates participation. The `[ModuleApi]` attribute therefore becomes a pure configurator: apply it only
to exclude a handler (`Exclude = true`) or to tag it into named scopes. A default
`[GenerateModuleApi]` facade includes every non-excluded handler in the owning module; a scoped
facade includes the handlers whose tags intersect its scopes. Scoped facades are the ISP tool:
a contract implementation that wants a lean dependency injects a small scoped facade instead of
the module-wide one.

The generator emits, per `[GenerateModuleApi]` interface: the method declarations (one per
handler, named after the handler type), an `internal` forwarder, and a DI registration wired into
the module's gated `ConfigureDefaultServices` via a new `AddModuleApi` hook. Diagnostics: `ELAPI001`
(must be partial), `ELAPI002` (must be top-level), `ELAPI003` (not under any module — warning),
`ELAPI004` (two handlers map to one method name).

**Handlers are resolved lazily, not constructor-injected.** Because a default facade spans every
handler in the module, eager constructor injection of each `IHandler<,>` would build *every*
handler's decorator chain (and its dependencies) just to construct the facade and call one method.
The forwarder therefore takes `IServiceProvider` and resolves the specific `IHandler<TRequest, TResponse>`
per call — you pay only for the methods you actually invoke.

### Generated output

For

```csharp
namespace Sales;

[GenerateModuleApi]
public partial interface ISalesApi;

public sealed class GetCustomer : IHandler<GetCustomer.Query, Result<GetCustomer.Response>> { /* ... */ }
```

the generator emits (shape, elided):

```csharp
// ISalesApi.ModuleApi.g.cs
public partial interface ISalesApi
{
    global::System.Threading.Tasks.ValueTask<global::Sales.Result<global::Sales.GetCustomer.Response>> GetCustomer(
        global::Sales.GetCustomer.Query request, global::System.Threading.CancellationToken ct = default);
}

internal sealed class ISalesApiForwarder(global::System.IServiceProvider services) : ISalesApi
{
    public global::System.Threading.Tasks.ValueTask<global::Sales.Result<global::Sales.GetCustomer.Response>> GetCustomer(
        global::Sales.GetCustomer.Query request, global::System.Threading.CancellationToken ct = default)
        => global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<global::Elarion.Abstractions.IHandler<global::Sales.GetCustomer.Query, global::Sales.Result<global::Sales.GetCustomer.Response>>>(services)
            .HandleAsync(request, ct);
}

// SalesModuleApiExtensions.g.cs — wired into the module's gated ConfigureDefaultServices (AddModuleApi hook)
public static class SalesModuleApiExtensions
{
    public static IServiceCollection AddSalesModuleApi(this IServiceCollection services)
    {
        services.TryAddScoped<global::Sales.ISalesApi, global::Sales.ISalesApiForwarder>();
        return services;
    }
}
```

### What we deliberately do NOT own

- **No generated contract forwarder.** A `[ModuleContract]` implementation is a trivial,
  hand-written adapter (inject the handler or the module API, map, forward). Generating it would
  replace a single line while forcing a DTO-mapping heuristic on users.
- **No mapping seam and no mapper dependency.** Contract↔handler (and, later, contract↔proto)
  mapping is the module's concern, expressed however the team likes.

## Recommended pattern

```csharp
// Module A — published contract (stable, public):
[ModuleContract]
public interface ICustomerLookup {
    ValueTask<Result<Customer>> GetAsync(CustomerId id, CancellationToken ct = default);
}

// Module A — typed in-process API over its own handlers (optional convenience):
[GenerateModuleApi]
public partial interface ICustomerModuleApi;

// Module A — the contract implementation: hand-written, internal, maps as it sees fit.
[Service]                                  // auto-registered + module-gated
internal sealed class CustomerLookup(ICustomerModuleApi api) : ICustomerLookup {
    public async ValueTask<Result<Customer>> GetAsync(CustomerId id, CancellationToken ct = default) {
        var result = await api.GetCustomer(new GetCustomer.Query(id.Value), ct);  // full pipeline
        return result.Map(r => new Customer(r.Id, r.Name));                       // module-owned mapping
    }
}

// Module B — depends only on the contract:
[Service]
internal sealed class OrderService(ICustomerLookup customers) { /* ... */ }
```

Injecting `Module A`'s internal `CustomerLookup` (or a handler) from `Module B` instead of
`ICustomerLookup` is reported by `ELMOD002`.

## The gRPC future

The contract interface is the stable seam across in-process *and* out-of-process backends. To
extract Module A into a service later, keep `ICustomerLookup` and replace the in-process
implementation with a generated gRPC/HTTP client implementing the same interface; Module B never
changes. The typed in-process API and the boundary analyzer are unaffected — Elarion's job at
the seam stays "own the convention and the analyzer," not "generate adapters."

## Consequences

**Positive**

- Direct cross-module calls have one sanctioned shape (a published contract), enforced by an
  analyzer rather than convention alone.
- Reuses existing machinery: `[Service]` registration, the decorated `IHandler<,>`, the
  `ConfigureDefaultServices` aggregation, and the `[EntityConfiguration]`/`[GenerateDbSets]` scope vocabulary.
- AOT/trim-friendly: typed-direct dispatch, no reflection, no serialization in-process.
- The contract seam is the clean extraction path to a real service boundary.

**Negative / accepted**

- Teams learn a third kind of cross-module surface (contract) alongside the two event planes.
- The typed in-process API is a convenience that overlaps with injecting `IHandler<,>` directly;
  it is intentionally optional. A module-wide facade is a wide internal surface unless narrowed
  with scopes.
- The boundary analyzer inspects only the dependency surface (constructor parameters, fields,
  properties), not method-body references such as service-locator resolution. This is a
  deliberate precision/noise trade-off.

## Deferred follow-ups

- **Renaming `[Handler]`.** It already drives both JSON-RPC and MCP, and the in-process API is
  deliberately *not* tied to it (it keys off handler discovery, not `[Handler]`). A
  transport-neutral rename (e.g. `[Operation]` with an exposure-surface flags enum) is a coherent
  but broad breaking change, intentionally out of scope here.
- **A generated gRPC/HTTP client backend** for `[ModuleContract]`, for when a module is extracted
  out of process. Not needed while everything is in-process.
