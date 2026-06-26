# ADR-0003: Predicate-based decorator attachment (`AppliesTo`)

- Status: Accepted
- Date: 2026-06-23
- Related: [ADR-0001](0001-event-transaction-phase.md) (transactions are application-owned),
  [decorator pipelines](../concepts/decorator-pipelines.mdx), the constraint-based decorator
  filtering added alongside the `ICommand`/`IQuery` markers.

## Context

Generated handler pipelines wrap each handler in an ordered list of decorators. A decorator can
already be filtered to a subset of handlers by a **generic constraint** (`where TRequest : ICommand`),
which the generator evaluates at compile time and elides where unsatisfied. That covers *one*
interface bound (an AND of "implements X"), but it cannot express a **union** or a **negation**.

The motivating case is the transaction decorator. "Should this handler open a unit of work?" is true
for **commands and integration-event handlers**, false for **queries and domain-event handlers**:

| Handler | Needs its own transaction? | Marker |
|---|---|---|
| Command | yes | `ICommand` |
| Query | no | `IQuery` |
| Domain-event handler (runs inline in the publisher's command) | no — shares the ambient one | `IDomainEvent` |
| Integration-event handler (fresh post-commit scope) | yes | `IIntegrationEvent` |

"Open a unit of work" is therefore `ICommand || IIntegrationEvent` — a union no `where` clause can
state. Some apps want still richer rules, e.g. attaching by a custom `[Transactional]` attribute on the
request.

[ADR-0001](0001-event-transaction-phase.md) keeps transactions **application-owned** — the framework
ships no transaction manager and no concrete decorator. So the fix must keep the decorator in
application code; it should make *attachment* more expressive, not move transaction semantics into the
framework.

## Decision

A decorator may declare an optional predicate:

```csharp
public static bool AppliesTo(HandlerMetadata handler);
```

When present, the generated factory **calls it once at pipeline-build time** (cached) and attaches the
decorator only when it returns `true` for that handler. It **composes with `where`**: the constraint governs
what the decorator body may call (type-safety); `AppliesTo` governs whether to attach.

The predicate receives [`HandlerMetadata`](../concepts/handlers.mdx) — the concrete handler type, its
request/response types, and `GetAttribute<T>()` — so it can attach on the request kind, the response type,
**or the handler's own attributes**:

```csharp
public sealed class TransactionDecorator<TRequest, TResponse>(/* ... */) : IHandler<TRequest, TResponse> {
    public static bool AppliesTo(HandlerMetadata handler) =>
        handler.RequestType.IsAssignableTo(typeof(ICommand)) ||
        handler.RequestType.IsAssignableTo(typeof(IIntegrationEvent));

    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // ... inner.HandleAsync, then commit on success / rollback on failure
    }
}
```

The transaction decorator stays trivial; domain-event handlers get no decorator at all (they ride the
publisher's ambient transaction), and queries get none either.

### One signature, on `HandlerMetadata`

There is exactly one predicate signature, on `HandlerMetadata` rather than `System.Type`. This is a deliberate
symmetry decision: the framework's own built-in decorators (caching, resilience, authorization) attach based on
a **handler** attribute (`[Cacheable]`, `[Resilient]`, `[RequirePermission]`), which a request-type-only
predicate cannot express. Giving custom decorators the handler-typed predicate means there is **no privileged
generator capability users cannot replicate** — the value the framework optimizes for. `HandlerMetadata` carries
`RequestType`, so request-kind checks (the common case) are unchanged in spirit (`handler.RequestType`), and the
generator passes the same cached metadata singleton it injects into decorator constructors.

We chose a single form over keeping a `Type`-based overload for backward compatibility: one signature is simpler
to teach and removes "which overload?" ambiguity. `DecoratorPredicate.Detect` recognizes only
`AppliesTo(HandlerMetadata)`; a non-`public` one is `ELPIPE001`, and a differently-shaped `AppliesTo` (e.g. the
older `AppliesTo(System.Type)`) is `ELPIPE002` — reported, never silently ignored (which would attach the
decorator unconditionally).

### Evaluated at run time, not symbolically

The predicate is **called**, not parsed. This was chosen over interpreting the method body at compile
time for three reasons:

1. **Any C# is allowed.** A symbolic interpreter would only ever support a restricted, growing subset
   (the EF "translation surface" problem). Calling the method supports arbitrary logic — including
   reflection over the request type, e.g. `request.IsDefined(typeof(TransactionalAttribute), inherit: true)`
   for a custom `[Transactional]` marker.
2. **It works across the metadata boundary.** A source generator can read attributes from referenced
   assemblies but not method *bodies* — so a symbolic analysis would be limited to decorators defined in
   the current compilation. A `static` method is **callable** via metadata, so a predicate on a
   *packaged* decorator works too.
3. **Simplicity.** No expression interpreter to build, maintain, or document.

### Caching and AOT

The generator emits, per closed handler type, a cached field initialized once at type init, and the
per-resolution factory only *reads* that field. A `PlaceOrder` command handler whose module pipeline
includes a `TxDecorator<,>` (here taking a `DbContext` dependency) generates roughly this
(fully-qualified names shortened for readability):

```csharp
public static class PlaceOrderRegistration {
    // AppliesTo runs ONCE, when this class's static fields initialize (registration/startup),
    // keyed by the request type. The factory below never calls it again.
    private static readonly bool __pipelineApplies0 =
        TxDecorator<PlaceOrderCommand, Result<PlaceOrderResult>>.AppliesTo(typeof(PlaceOrderCommand));

    public static IServiceCollection AddPlaceOrder(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) {
        services.Add(new ServiceDescriptor(typeof(PlaceOrder), typeof(PlaceOrder), lifetime));
        services.Add(new ServiceDescriptor(
            typeof(IHandler<PlaceOrderCommand, Result<PlaceOrderResult>>),
            sp => {
                IHandler<PlaceOrderCommand, Result<PlaceOrderResult>> handler =
                    sp.GetRequiredService<PlaceOrder>();
                if (__pipelineApplies0)                       // reads the cached bool, per resolution
                    handler = new TxDecorator<PlaceOrderCommand, Result<PlaceOrderResult>>(
                        handler, sp.GetRequiredService<ShopDbContext>());
                // tracing is emitted last, so it is the outermost decorator
                handler = new TracingDecorator<PlaceOrderCommand, Result<PlaceOrderResult>>(handler, "PlaceOrder");
                return handler;
            },
            lifetime));
        return services;
    }
}
```

So the predicate runs **once per closed handler type** (at registration/startup), never per request:
the per-resolution factory just tests `__pipelineApplies0`. The conditional is emitted for **every**
in-scope handler — a query's `__pipelineApplies0` is simply `false` at runtime, so its `if` skips the
decorator. That is the runtime-vs-compile-time trade-off below: the decorator type is referenced behind
the `if` in each in-scope chain rather than elided.

The emitted call is a direct static invocation plus `typeof` (an `ldtoken`), so the framework's
generated code is **NativeAOT- and trim-safe**. The predicate *body* is the application's code; the
reflection it typically uses (`IsAssignableTo`, attribute presence via `Type.IsDefined`/`GetCustomAttribute`)
is AOT-safe because the types it names are statically rooted by `typeof`.

`AppliesTo` must be **`public`** so the generated registration (emitted into the consuming assembly,
and for referenced decorators) can call it. A non-public `AppliesTo` is reported `ELPIPE001`. Because
the predicate is plain, runnable C#, it can be unit-tested directly
(`TransactionDecorator<,>.AppliesTo(typeof(CreateOrder.Command))`).

## Consequences

**Positive**

- Decorator attachment can express any rule over the request type (unions, negations, attribute checks),
  not just a single interface bound.
- The canonical transaction decorator is correct in all four cases (command/query/domain/integration)
  while staying application-owned (ADR-0001 intact).
- Works for decorators in referenced assemblies, and is AOT/trim-safe and cached to one evaluation per
  closed handler type.

**Negative / accepted**

- Attachment is a **runtime** decision, so the decorator type is referenced in every in-scope handler's
  generated chain (a conditional), not compile-time elided. When the rule is a simple type bound and you
  want elision/trimming, use a `where` constraint instead.
- A read-only handler that nonetheless establishes a unit of work (e.g. a read-only integration consumer
  matching `IIntegrationEvent`) opens a transaction it doesn't use — harmless.
- The predicate must be `public` and total; a throwing predicate surfaces as a type-initialization error.
- A decorator that needs the *handler's own* type or attributes must **not** read them from
  `inner.GetType()`: because decorators wrap innermost-first, `inner` is the concrete handler only when
  the decorator is innermost, so an attribute-driven check (e.g. authorization) silently **fails open**
  at any other position. Decorators declare a `HandlerMetadata` constructor parameter instead — the
  generator supplies the concrete handler type position-independently. The seam offers a correct path
  but does not forbid the `inner.GetType()` anti-pattern from compiling (a reliable analyzer for it was
  judged too fragile / false-positive-prone), so it is documented in
  [decorator pipelines](../concepts/decorator-pipelines.mdx#reading-the-handlers-attributes-do-not-use-innergettype).

## Implementation

- `DecoratorPredicate.Detect` (`Elarion.Generators`) finds a `public static bool AppliesTo(HandlerMetadata)`
  on a decorator (reporting `ELPIPE001` if non-public, or `ELPIPE002` for a differently-shaped `AppliesTo`).
- `HandlerRegistrationGenerator` flags such decorators (`DecoratorInfo.HasAppliesTo`), emits the cached field
  — calling the predicate with the `__handlerMetadata` singleton — and wraps the decorator's construction in
  `if (__pipelineApplies{i})`.
- Documented in [decorator pipelines](../concepts/decorator-pipelines.mdx); the tutorial's transaction
  decorator uses `AppliesTo`.
