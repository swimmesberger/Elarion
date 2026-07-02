/**
 * "Batteries included. Sockets standard." — the packaging philosophy: every
 * capability is a public seam in the dependency-light core plus an opt-in
 * battery package that brings its own dependency. HTML grid (no interactivity
 * needed); the dashed top edge on each card is the socket.
 */

const batteries = [
  {
    seam: 'IHandlerCache',
    name: 'HybridCache, two tiers',
    body: 'In-process L1 plus a distributed L2. The recommended L2 is a PostgreSQL UNLOGGED table — reuse the database you already run instead of operating Redis.',
    packages: 'Elarion.Caching · .PostgreSql',
  },
  {
    seam: 'IResiliencePipelineRunner',
    name: 'Polly, when asked',
    body: '[Resilient] handlers and deferred scheduler retries run named retry/timeout pipelines. Polly enters your build the day a handler asks — not before.',
    packages: 'Elarion.Resilience',
  },
  {
    seam: 'IFeatureFlagService',
    name: 'OpenFeature underneath',
    body: '[FeatureGate] works against any OpenFeature provider. Microsoft.FeatureManagement ships as the batteries-included, config-driven default.',
    packages: 'Elarion.FeatureFlags.*',
  },
  {
    seam: 'IIntegrationEventBus',
    name: 'A transactional outbox',
    body: 'Events recorded in the same transaction as your data, delivered at-least-once after commit. An in-memory bus for development — same interface.',
    packages: 'Elarion.Messaging.Outbox · .InMemory',
  },
  {
    seam: 'IBlobStore',
    name: 'Streaming blobs in Postgres',
    body: 'Streaming-first storage with direct and resumable (tus) uploads and a pending → commit lifecycle for attach-then-reference flows.',
    packages: 'Elarion.Blobs.*',
  },
  {
    seam: 'ISettingsStore',
    name: 'Runtime settings, watchable',
    body: 'Global and per-user key/value settings in EF Core with optimistic concurrency, surfaced live through IConfiguration and IOptionsMonitor.',
    packages: 'Elarion.Settings.*',
  },
];

export function Batteries() {
  return (
    <div>
      {/* the core: seams only */}
      <div className="rounded-[4px] border border-(--line) px-6 py-5">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <span className="font-display text-lg font-semibold tracking-[-0.01em] text-fd-foreground">
            Elarion core
          </span>
          <span className="font-mono text-xs text-fd-muted-foreground">
            Elarion + Elarion.Abstractions
          </span>
        </div>
        <p className="mt-2 max-w-3xl text-sm leading-relaxed text-(--body)">
          References Microsoft.Extensions <span className="font-mono text-[0.92em] text-fd-foreground">*.Abstractions</span> packages
          and nothing else. Every capability below is a public seam here — the attribute and the
          decorator live in the core; the heavy dependency lives one package out.
        </p>
      </div>

      {/* the batteries, plugged into their sockets */}
      <div className="mt-5 grid gap-5 md:grid-cols-2 lg:grid-cols-3">
        {batteries.map((battery) => (
          <div key={battery.seam} className="flex flex-col rounded-[4px] border border-(--line) border-t-2 border-t-fd-primary/40 p-5">
            <p className="font-mono text-xs text-fd-primary">{battery.seam}</p>
            <h3 className="mt-2 font-display text-base font-semibold tracking-[-0.01em] text-fd-foreground">
              {battery.name}
            </h3>
            <p className="mt-2 grow text-sm leading-relaxed text-(--body)">{battery.body}</p>
            <p className="mt-4 border-t border-(--line-soft) pt-3 font-mono text-[0.7rem] text-fd-muted-foreground">
              {battery.packages}
            </p>
          </div>
        ))}

        {/* the empty socket */}
        <div className="flex flex-col justify-center rounded-[4px] border border-dashed border-(--line) p-5">
          <p className="font-mono text-xs text-fd-muted-foreground">your implementation</p>
          <h3 className="mt-2 font-display text-base font-semibold tracking-[-0.01em] text-fd-foreground">
            Every seam is public
          </h3>
          <p className="mt-2 text-sm leading-relaxed text-(--body)">
            Don&apos;t like a battery? Register your own implementation of the seam — no handler,
            attribute, or generated line changes.
          </p>
        </div>
      </div>
    </div>
  );
}
