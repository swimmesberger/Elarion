# ADR-0012: Referencing dynamic variables in attributes — form follows tier

- Status: Accepted
- Date: 2026-06-29
- Related: [ADR-0003](0003-decorator-attachment-predicates.md) (logic graduates to generator-wired
  code — the `AppliesTo` idiom this ADR generalizes), [ADR-0006](0006-incremental-source-generator-conventions.md)
  (the generator must validate and bind these references incrementally), [ADR-0009](0009-authorization-building-blocks.md)
  (authorization is declarative and AOT-clean — the first consumer is resource authorization),
  [ADR-0011](0011-runtime-settings-subsystem.md) (the settings-backed `IConfiguration` source feeds the
  ambient tier), and the [variable substitution](../concepts/variable-substitution.mdx) concept doc.

## Context

Elarion already ships **variable substitution**: Spring-style `${key}` / `${key:-default}` placeholders
resolved from a pluggable `IVariableSource` (`bool TryGetValue(string key, out string? value)`), with a
whole-value (`Resolve`) and an embedded (`Substitute`) model. The shipped source reads `IConfiguration`, so it
sees appsettings, environment, and database-backed [settings](../concepts/settings.mdx); the scheduler uses it
to make a job's cron/interval/enabled-flag runtime-changeable. Every value it resolves is **ambient,
string-keyed, and context-free** — `TryGetValue(key)` has no notion of *which call* is in flight, and it
returns a `string` resolved at run time.

A class of features now needs to reference a *different* kind of value from an **attribute**: **per-invocation
dynamic state** — most immediately, a property of the handler's request DTO (the resource id for resource-based
authorization), and prospectively the current user / claims. Two things make this genuinely unlike the ambient
case:

1. **Context.** `request.Id` exists only *during one handler invocation*. The ambient `IVariableSource` seam
   has no slot for per-call context, by design.
2. **Resolution mechanism.** A request property is known **by type at compile time**. Reading it *at run time*
   from an opaque string key would mean reflecting over `TRequest` — which the framework forbids on hot paths
   (AOT/trim-clean, no runtime scanning). Elarion's answer to "bind to a typed shape" is everywhere the same:
   **source generation**, not reflection.

The obvious-but-wrong move is to import a Spring-Expression-Language-style mini-grammar into attribute strings:
`[RequirePermission("read", "#id")]`, and from there `#order.total > 100 ? 'priority' : 'normal'`. SpEL is a
full expression language — property/index access, method invocation, operators, ternary/Elvis, collection
selection/projection, type and bean references. Adopting any of its *computational* surface means **logic lives
in strings**: unparseable by tooling, untestable, unrefactorable, and an ever-growing interpreter to maintain
(the same "translation surface" trap [ADR-0003](0003-decorator-attachment-predicates.md) rejected for decorator
attachment). Elarion's established instinct is the opposite — define logic as ordinary C# (`AppliesTo`
predicates, decorator classes, `IQueryAuthorizer<T>`) that a generator *wires in at the right place*.

Backward compatibility with the exact current `${...}` shape is **not** a constraint on this decision — we
chose freely — but we keep `${...}` for the ambient tier because it already fits the ambient tier perfectly.

## Decision

The headline rule, applied across all current and future features:

> **An attribute *references* a value by path; it never *computes*. Computation graduates to
> generator-wired C# code.**

Concretely, dynamic-variable references are modeled in **two tiers, and the notation follows the tier** — each
form is the one that is honest about how the value is actually resolved:

### Tier 1 — compile-time: request-shape references are typed C#

A reference *into the handler's request* (the `#id` analog) is expressed as **compile-checked C#**, never as a
string expression:

```csharp
[RequireResource(typeof(Contact), Operation = "read", Id = nameof(GetContactQuery.Id))]
public sealed class GetContactQuery : IQuery { public Guid Id { get; init; } }
```

- The value is a **path** — `nameof(Req.Id)`, or a dotted path `nameof(Req.Customer) + "." + nameof(...)`
  reduced by the generator to `Customer.Id`. A path is pure *addressing*, not logic.
- The source generator **validates the path against `TRequest`** at compile time and emits a zero-reflection
  typed accessor (`r => r.Id`). A path that names no such member is a **diagnostic**, not a runtime surprise.
- Generated access is direct member access (`ldfld`/`callvirt` on a known property) — **NativeAOT- and
  trim-safe**, with no reflection on the request.

When a *value* needs more than a path — a fallback, a derivation, two fields combined — it **graduates to
code**: a generator-discovered `static` selector on the type, the same shape and rationale as
[ADR-0003](0003-decorator-attachment-predicates.md)'s `AppliesTo`:

```csharp
// Graduation: still typed C#, still generator-wired — never a richer string.
public static object? ResourceId(GetContactQuery r) => r.OverrideId ?? r.Id;
```

Such a method is **called, not parsed**: any C# is allowed, it works across the metadata boundary (a packaged
type's `static` method is callable; its *body* is not readable by a generator), and there is no expression
interpreter to build or maintain. This is the exact ladder ADR-0003 established for decorator attachment,
reused for "which value."

### Tier 2 — run time: ambient references stay `${...}` strings

Config, settings, environment — and, *only if* a concrete need ever appears, identity scopes — remain
`${key}` / `${key:-default}` resolved at run time through `IVariableSource`. This is the **existing** mechanism,
unchanged. It is the right tool precisely because an ambient value is **not** known at compile time, **is**
inherently a string-keyed lookup, **is** runtime-changeable (the settings/observable story), and **does** compose
into larger templates (`Substitute`). Forcing it into the typed tier would buy nothing; forcing the request tier
into it would drag reflection onto the hot path.

### Why the split (rather than one unified form)

| | Request-shape value | Ambient value |
|---|---|---|
| Known at | compile time, **by type** | run time, by string key |
| Natural resolution | typed member access (generated) | string lookup (`IVariableSource`) |
| Reflection-free? | only if generated | inherently (string in, string out) |
| Runtime-changeable? | no — it is the call's input | yes — the source reloads |
| Composable into templates? | not needed (single value) | yes (`Substitute`) |
| Honest notation | `nameof` / selector (C#) | `${...}` (string) |

Two genuinely different kinds of value resolved by two genuinely different mechanisms get two notations, each
matching its mechanism — instead of one notation that lies about one of them. This is "form follows tier."

### What we deliberately exclude (the reference-vs-compute line)

We adopt **only path/index addressing** from the SpEL spectrum, and stop hard before computation. Everything
below the line is **code**, wired by a generator:

| SpEL capability | Example | Elarion |
|---|---|---|
| Property / index path | `#order.customer.id`, `#list[0]` | ✅ kept — pure addressing |
| Method calls | `#name.toUpperCase()` | ❌ → selector method / decorator |
| Operators, comparison | `#age > 18`, `#a + #b` | ❌ → code |
| Ternary / Elvis | `#x != null ? #x : 'd'` | ❌ → code |
| Collection select / project | `#users.?[age > 18]` | ❌ → `IQueryAuthorizer<T>` / LINQ |
| Type / bean references | `T(Math).PI`, `@svc.foo()` | ❌ → inject the service |

If a future feature wants to *embed* a dynamic reference inside a composite string (a templated audit message,
a computed cache key), that — and only that — is the trigger to reconsider surfacing the request/identity tier
through the runtime `${...}` model (via a **scoped** `IVariableSource` over the already-scoped `ICurrentUser`,
never via reflection). It is explicitly **out of scope** here; we will not build it speculatively.

### First consumer: resource-based authorization

Resource authorization ([ADR-0013](0013-resource-and-data-level-authorization.md)) is the first feature built on this model, and it shows the split is the
*natural* shape, not an imposition:

- **Point check (Leg A).** The only dynamic reference it needs in an attribute is *which request property is the
  resource id* — a single request-shape path: `Id = nameof(GetContactQuery.Id)`. Tier 1, generated selector, no
  `#id` string.
- **List filter & sharing logic (Leg B).** "Owner OR shared-with-role" is **logic**, and it already lives in
  code wired by a generator — `[ResourceFilter<T>]` emits an `IQueryAuthorizer<T>`, and any rule beyond the
  conventional ones is a hand-written `IQueryAuthorizer<T>`. This is the canonical "compute in generated-wired
  code, not in a string" example; this ADR simply names the principle it already follows.

So resource authorization needs *only* the trivial request-path reference from Tier 1; everything with logic was
always going to be code.

## Consequences

**Positive**

- Request-shape references are **type-checked and refactor-checked**: `nameof`/selectors move with renames, and
  a stale path is a compile error, not a runtime 500.
- The framework's generated access stays **AOT/trim-clean** — no reflection on requests.
- **No new language** to design, parse, document, or grow. Tooling, debuggers, and unit tests see ordinary C#.
- `${...}` stays focused on what it is good at (ambient, runtime-changeable, composable), rather than slowly
  mutating into a half-SpEL.
- Logic uniformly lives in **testable C#** with a clear graduation ladder (path → `static` selector →
  `IQueryAuthorizer`/decorator), consistent with ADR-0003.

**Negative / accepted**

- There are **two forms** (a `${...}` string for ambient, typed C# for dynamic). This is deliberate — they
  resolve by different mechanisms — but it is one more thing to learn than a single grammar would be.
- A request reference cannot be **embedded** in a larger string and cannot be **composed** with ambient values
  in one template. Accepted: no current feature needs it, and it is the documented trigger to revisit the
  runtime tier rather than a reason to build an expression language now.
- Referencing **user claims declaratively in an attribute is not supported** today. User/identity context is
  consumed in code (handlers, `IAuthorizer`, `IQueryAuthorizer`) where it belongs; if a declarative need
  appears, it enters through the runtime tier as a scoped source, not through reflection.
- The dotted-path mini-form is intentionally *less* than SpEL; a team wanting in-attribute computation will not
  find it and must write code. That is the point.

## Implementation

- **Tier 1 binding** is emitted by the consuming feature's generator (for resource authorization, by
  `HandlerRegistrationGenerator` alongside the existing `[Require*]` attach): parse the `nameof`/dotted path,
  validate it against `TRequest`, emit a typed selector (e.g. `IResourceIdSelector<TRequest>` /
  `r => r.Id`) and a diagnostic for an unresolvable path. The selector is supplied to the decorator the same way
  `HandlerMetadata` is — position-independently, never via `inner.GetType()`.
- **Graduation selector** discovery mirrors `DecoratorPredicate.Detect` (ADR-0003): a `public static` method of
  the documented shape is recognized, flagged on the model, called (never parsed), and reported when
  mis-shaped.
- **Tier 2** is the existing `Elarion.Abstractions.Substitution` building block (`IVariableSource`,
  `VariableSubstitution`, `ConfigurationVariableSource`) — unchanged by this ADR.
- All generator work follows [ADR-0006](0006-incremental-source-generator-conventions.md): value-equatable
  models, diagnostics-as-data, byte-identical output, cache-reuse tests.
