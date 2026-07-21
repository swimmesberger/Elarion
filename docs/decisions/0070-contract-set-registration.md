# ADR-0070: Contract-set registration — unconditional composition of one contract's implementation set

- Status: Accepted
- Date: 2026-07-21
- Related: [ADR-0006](0006-incremental-source-generator-conventions.md),
  [ADR-0017](0017-dependency-light-core.md).

## Context

Elarion's registration philosophy — boilerplate derived from declarations, never hand-maintained, so a
feature touches only its own files — is served by `[Service]` and the module bootstrapper. That
machinery is deliberately **module-shaped**: per-service extensions are aggregated only for services
under an `[AppModule]` namespace, and the aggregation is invoked gated on `Modules:{Name}:Enabled`.
Both properties are correct for business features and wrong for a class of registrations module-less
host assemblies have: **infrastructure seams** — N implementations of one contract composed into a
routing/dispatch table at boot (protocol packet bindings, codec/framer catalogs, pipeline stages).

- There is no module. The implementations live in a connection-host assembly next to the framer and
  codec; wrapping them in an `[AppModule]` misstates what they are.
- Config gating is a hazard, not a feature. The host usually has its own top-level enable switch
  (turn the whole listener off); a second, per-module gate that silently empties the dispatch table
  while the listener stays up produces a server that boots deaf. The composition of a transport's
  routing table should be unconditional, with the consuming registry free to fail loudly on an
  invalid set.
- Falling back to `[Service]` without a module yields per-service extension methods that nothing
  calls — callers must hand-invoke one per implementation, recreating the hand-maintained central
  list the philosophy exists to eliminate.

The motivating field evidence: a game-server host with one packet-binding implementation per opcode,
resolved as `IEnumerable<TBinding>` by a frozen registry that validates the set at startup. The
downstream repository filled the gap with a ~90-line repo-local generator following Elarion's
conventions — the concept, not the effort, belongs in the framework.

## Decision

A contract set is declared as a **host-authored partial method**: a method-level attribute in
`Elarion.Abstractions` marks a `static partial IServiceCollection` extension method, and the
generator fills in the body (the anchor shape is prior art from ServiceScan.SourceGenerator):

```csharp
public static partial class PacketBindingRegistrations {
    [GenerateContractSetRegistration(typeof(IPacketBinding))]
    public static partial IServiceCollection AddPacketBindings(this IServiceCollection services);
}
```

The optional `Scope` named argument (`ServiceScope`, default `Singleton` — infrastructure seams are
boot-composed) selects the lifetime. There is deliberately no `MethodName` knob and no derived
type/method naming or namespace-placement convention: the host names and places the method it calls,
go-to-definition works, and "pulled by the host" is literal — the declaration *is* the host's
composition call site.

`ContractSetRegistrationGenerator` in `Elarion.Generators` discovers every non-abstract, non-generic
class **declared in the same compilation** that is assignable to the contract and implements the
method:

```csharp
partial class PacketBindingRegistrations {
    public static partial IServiceCollection AddPacketBindings(this IServiceCollection services) {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPacketBinding, AuthSessionBinding>());
        // … one line per implementation, sorted by fully qualified name …
        return services;
    }
}
```

Key semantics:

- **`TryAddEnumerable`, not `Add`** — calling the method twice (host composition mistakes, test
  fixtures composing the same defaults) must not duplicate the set. This is the semantic the
  hand-written version always gets wrong first.
- **Pulled, not pushed** — the method is not invoked by any bootstrapper and is not config-gated.
  The host calls it from its own composition root, exactly once, unconditionally. This is the
  documented contrast with `[Service]`.
- **Compilation-local discovery** — the assembly that declares the seam owns its implementations.
  Referenced assemblies contributing implementations would reintroduce spooky action at a distance;
  a host wanting external implementations registers them explicitly.
- **Deterministic output** — implementations sorted by fully qualified name, `global::`-qualified;
  the pipeline is incremental-cache-correct (`EquatableArray` models, diagnostics as data, tracked
  nodes with cache tests per ADR-0006).

Diagnostics: `ELSG014` (contract not an interface/abstract class, or open generic — error),
`ELSG015` (zero implementations — warning; the empty method still exists, the consuming registry
decides whether empty is fatal), `ELSG016` (same contract declared twice in one assembly — error;
the first declaration by file position gets the real implementation), `ELSG017` (generic
implementation — error, mirrors ELSG003), `ELSG018` (implementation also carries `[Service]`
resolving to the same contract — warning; the author must pick one mechanism per contract),
`ELSG019` (annotated method does not match the required `static partial IServiceCollection
Name(this IServiceCollection services)` shape — error). When a declaration is rejected for its
contract (`ELSG014`/`ELSG016`), the partial method is still implemented empty so the author sees one
clear diagnostic instead of a cascading CS8795.

## Considered alternatives

- **An assembly-level attribute** (`[assembly: GenerateContractSetRegistration(typeof(IPacketBinding))]`
  emitting a conventionally named `internal static {Name}ContractSet.Add{Name}Implementations()` in
  the contract's namespace). Implemented first, then replaced by the partial-method anchor: the
  assembly shape needed a `MethodName` escape hatch, a leading-`I`-stripping name derivation, a
  generic-contract disambiguation rule, and a namespace-placement convention — all of which dissolve
  when the host authors the method. The partial-method anchor also makes the generated code
  discoverable (go-to-definition on the call site) at the cost of two extra declaration lines.
- **A general compile-time scanner** (ServiceScan-style `TypeNameFilter` wildcards,
  `FromAssemblyOf` cross-assembly scanning, `AsImplementedInterfaces`). Rejected: string-pattern
  matching reintroduces convention-by-name registration, and cross-assembly scanning contradicts the
  seam-owner principle below. The narrow contract-set semantics (one explicit contract,
  compilation-local, `TryAddEnumerable`) are the point, not a limitation.
- **Module-less `[Service]` aggregation.** Widening `[Service]` to aggregate outside modules changes
  gating semantics everyone relies on; rejected — additive only, nothing about `[Service]`, module
  aggregation, or gating changes.
- **Cross-assembly discovery via the Elarion manifest.** Deferred; it contradicts the seam-owner
  principle above and no field evidence needs it.
- **Keyed services / ordering guarantees.** Consumers that need an ordered set sort by their own key
  (a registry keyed by opcode already does); determinism of the emitted source is guaranteed,
  resolution order semantics are MS.DI's.
- **Compile-time singleton dependency verification** (ELSG011-style) for set members: possible later
  extension, not required for v1 — DI fails as any singleton does.

## Consequences

- Host assemblies compose transport routing tables from declarations with no hand-maintained central
  list, and a disabled feature module can never silently empty them.
- A second registration mechanism exists beside `[Service]`; the "when do I use which" rule is
  documented in the services concept (module services vs. contract sets) and enforced at the overlap
  by `ELSG018`.
- Downstream repositories with repo-local equivalents can delete them and call the framework-emitted
  method instead.
