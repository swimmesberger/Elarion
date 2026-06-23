# ADR-0004: Handler result caching (`[Cacheable]` / `[CacheInvalidate]`)

- Status: Accepted
- Date: 2026-06-23
- Related: [decorator pipelines](../concepts/decorator-pipelines.mdx),
  [ADR-0003](0003-decorator-attachment-predicates.md) (decorators are composed by the same generator),
  the [caching concept doc](../concepts/caching.mdx) (usage).

## Context

A handler that reads data should be cacheable, and a handler that mutates it should evict the now-stale
entries — declaratively, per handler, without each handler hand-writing key construction, serialization,
scope handling, or cache plumbing. The framework already composes a per-handler **decorator pipeline**
at compile time (see [decorator pipelines](../concepts/decorator-pipelines.mdx)); caching should be a
decorator in that same pipeline, not a parallel runtime mechanism.

Two forces shape the design:

1. **No runtime reflection on the hot path.** Elarion preserves trimming/AOT friendliness and avoids
   hidden runtime discovery. Scanning a handler's attributes, or reflecting over request properties to
   build a cache key *at request time*, would undercut both and add per-call cost.
2. **Caching must never make failures sticky.** A transient error returned as a failed `Result<T>`
   must not be stored and replayed.

## Decision

Caching is **declared by an attribute and realized as a compile-time generated decorator plus a
generated policy class** — there is no runtime attribute scanning and no runtime property reflection to
build keys.

### Attributes (`Elarion.Abstractions.Caching`)

Both are `[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]`.

- **`[Cacheable(params string[] tags)]`** marks a read handler. Named options:
  `DurationSeconds` (default `60`), `Scope` (`HandlerCacheScope.CurrentUser` = `0`, the default, or
  `Global` = `1`), and `KeyProperties` (default empty → **all** public request properties form the key;
  otherwise only the named ones).
- **`[CacheInvalidate(params string[] tags)]`** marks a mutating handler. Named option: `Scope`
  (same enum, default `CurrentUser`). Invalidation runs **only after** the inner handler returns a
  successful `Result<T>`.

### Generated output

`HandlerRegistrationGenerator` parses the attributes (`HandlerRegistrationGenerator.Cache.cs`) and emits
into the handler's registration class (`HandlerRegistrationGenerator.Emit.cs`):

- a `CacheDecorator<TRequest, TResponse>` (read-through) or a `CacheInvalidationDecorator<TRequest,
  TResponse>`, inserted into the same decorator chain as tracing/resilience/pipeline decorators; and
- a nested `private sealed class {Handler}CachePolicy : IHandlerCachePayloadPolicy<TRequest, TResponse>`
  (or `{Handler}CacheInvalidationPolicy : IHandlerCacheInvalidationPolicy`) holding the resolved
  metadata.

A `GetClient` read handler marked `[Cacheable("clients", DurationSeconds = 120)]` with request
`GetClientQuery(Guid Id)` generates roughly this (fully-qualified names shortened for readability):

```csharp
public static class GetClientRegistration {
    public static IServiceCollection AddGetClient(this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) {
        services.Add(new ServiceDescriptor(typeof(GetClient), typeof(GetClient), lifetime));
        services.Add(new ServiceDescriptor(
            typeof(IHandler<GetClientQuery, Result<GetClientResponse>>),
            sp => {
                IHandler<GetClientQuery, Result<GetClientResponse>> handler =
                    sp.GetRequiredService<GetClient>();
                handler = new CacheDecorator<GetClientQuery, Result<GetClientResponse>>(
                    handler,
                    sp.GetRequiredService<IHandlerCache>(),
                    new GetClientCachePolicy());
                handler = new TracingDecorator<GetClientQuery, Result<GetClientResponse>>(handler, "GetClient");
                return handler;
            },
            lifetime));
        return services;
    }

    private sealed class GetClientCachePolicy
        : IHandlerCachePayloadPolicy<GetClientQuery, Result<GetClientResponse>> {
        private static readonly string[] CacheTags = new[] { "clients" };

        public string KeyPrefix => "handler-cache:v1:Shop.Clients.GetClient";
        public TimeSpan Expiration => TimeSpan.FromSeconds(120);
        public HandlerCacheScope Scope => (HandlerCacheScope)0;          // CurrentUser
        public IReadOnlyList<string> Tags => CacheTags;

        // Built from the request's public properties at COMPILE time — no runtime reflection.
        public string CreateKey(GetClientQuery request) =>
            HandlerCacheKey.Build(HandlerCacheKey.Part("Id", request.Id));

        // Only the success value is serialized; a failed Result<T> is never stored.
        public string Serialize(Result<GetClientResponse> response, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(response.Value, options);

        public Result<GetClientResponse> Deserialize(string payload, JsonSerializerOptions options) {
            var value = JsonSerializer.Deserialize<GetClientResponse>(payload, options);
            return Result<GetClientResponse>.Success(value!);
        }
    }
}
```

A `[CacheInvalidate("clients")]` mutating handler instead generates a `CacheInvalidationDecorator<,>`
plus a minimal `{Handler}CacheInvalidationPolicy` carrying only `Scope` and `Tags`.

The key insight is that **everything reflection-shaped is resolved at build time**: the tag list, the
duration, the scope, and — critically — the set of request properties forming the key (`CreateKey` is
emitted as concrete `request.Property` reads via `HandlerCacheKey.Part`, see
`ResolveCacheKeyProperties`/`AppendCreateKeyMethod`). `Serialize`/`Deserialize` unwrap `Result<T>` so
only the success `Value` is cached (`TryGetResultValueFqn`).

### Build-time validation

The generator reports misuse at build time (`HandlerRegistrationGenerator.Cache.cs`
`ValidateCacheMetadata`, tracked in `AnalyzerReleases.Unshipped.md`):

- `ELCACHE001` — a handler is both `[Cacheable]` and `[CacheInvalidate]`.
- `ELCACHE002` — required cache tags are missing.
- `ELCACHE003` — an invalid tag (empty/whitespace, or the reserved `*`).
- `ELCACHE004` — a non-positive `DurationSeconds`.

## Consequences

**Positive**

- The cache hot path is **reflection-free and AOT/trim-safe**: concrete generic decorator types, a
  statically built key, and `JsonSerializer` over a statically-known value type — no runtime attribute
  or property discovery.
- Only successful `Result<T>` responses are cached, so a transient failure cannot become sticky.
- Keys and tags carry an explicit **scope**: `CurrentUser` isolates entries by the authenticated user
  (the id is hashed into the physical key), `Global` shares them; reads and invalidations must agree on
  tags and scope to match.
- Caching composes with the rest of the pipeline (tracing/resilience/predicate-attached decorators)
  through one deterministic, inspectable generated registration.

**Negative / accepted**

- Cache behavior is fixed at compile time; changing tags, duration, scope, or key properties is a
  recompile, not a runtime toggle (consistent with the framework's generate-don't-reflect stance).
- A handler cannot be both cacheable and cache-invalidating (`ELCACHE001`); the read/write split is
  intentional.
- Read/write coupling is **by tag**: an invalidation only evicts reads that share its tags and scope, so
  a mismatch silently fails to evict. This is the cost of decoupling readers from writers.

## Implementation

- Attributes and policy contracts: `src/Elarion.Abstractions/Caching/`
  (`CacheableAttribute`, `CacheInvalidateAttribute`, `IHandlerCachePolicy`, `HandlerCacheScope`,
  `HandlerCacheKey`, `CacheDecorator`, `CacheInvalidationDecorator`).
- Parsing/validation: `HandlerRegistrationGenerator.Cache.cs`
  (`ParseCacheable`, `ParseCacheInvalidation`, `ResolveCacheKeyProperties`, `ValidateCacheMetadata`).
- Emission: `HandlerRegistrationGenerator.Emit.cs`
  (`AppendCacheDecorator`, `AppendCacheInvalidationDecorator`, `AppendCachePolicy`,
  `AppendCreateKeyMethod`, `AppendPayloadMethods`).
- Usage and runtime backing (`HybridCache`) are documented in
  [the caching concept doc](../concepts/caching.mdx).
