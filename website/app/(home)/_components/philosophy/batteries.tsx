/**
 * "Batteries included. Sockets standard." — drawn, not just told: the core is
 * a rail with a socket strip; every battery hangs from its public seam via a
 * plug. Hovering a battery unplugs it (pure CSS) — the "removable" argument
 * as a gesture. Icons follow the floor plan's stroke style.
 */

type BatteryIconKind = 'bolt' | 'shield' | 'toggle' | 'mail' | 'box' | 'sliders';

const batteries: {
  seam: string;
  name: string;
  body: string;
  packages: string;
  icon: BatteryIconKind;
}[] = [
  {
    seam: 'IHandlerCache',
    name: 'HybridCache, two tiers',
    body: 'In-process L1 plus a distributed L2. The recommended L2 is a PostgreSQL UNLOGGED table — reuse the database you already run instead of operating Redis.',
    packages: 'Elarion.Caching · .PostgreSql',
    icon: 'bolt',
  },
  {
    seam: 'IResiliencePipelineRunner',
    name: 'Polly, when asked',
    body: '[Resilient] handlers and deferred scheduler retries run named retry/timeout pipelines. Polly enters your build the day a handler asks — not before.',
    packages: 'Elarion.Resilience',
    icon: 'shield',
  },
  {
    seam: 'IFeatureFlagService',
    name: 'OpenFeature underneath',
    body: '[FeatureGate] works against any OpenFeature provider. Microsoft.FeatureManagement ships as the batteries-included, config-driven default.',
    packages: 'Elarion.FeatureFlags.*',
    icon: 'toggle',
  },
  {
    seam: 'IIntegrationEventBus',
    name: 'A transactional outbox',
    body: 'Events recorded in the same transaction as your data, delivered at-least-once after commit. An in-memory bus for development — same interface.',
    packages: 'Elarion.Messaging.Outbox · .InMemory',
    icon: 'mail',
  },
  {
    seam: 'IBlobStore',
    name: 'Streaming blobs in Postgres',
    body: 'Streaming-first storage with direct and resumable (tus) uploads and a pending → commit lifecycle for attach-then-reference flows.',
    packages: 'Elarion.Blobs.*',
    icon: 'box',
  },
  {
    seam: 'ISettingsStore',
    name: 'Runtime settings, watchable',
    body: 'Global and per-user key/value settings in EF Core with optimistic concurrency, surfaced live through IConfiguration and IOptionsMonitor.',
    packages: 'Elarion.Settings.*',
    icon: 'sliders',
  },
];

function BatteryIcon({ kind }: { kind: BatteryIconKind }) {
  const common = {
    fill: 'none' as const,
    stroke: 'var(--accent-gen)',
    strokeWidth: 1.7,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
  };
  return (
    <svg viewBox="0 0 24 24" className="size-[22px] shrink-0" aria-hidden xmlns="http://www.w3.org/2000/svg">
      {kind === 'bolt' && <path d="M13 2.5 L5.5 13.5 h5 L10 21.5 L18.5 10 h-5 Z" {...common} />}
      {kind === 'shield' && (
        <>
          <path d="M12 3 L19 6 v6 c0 4.3-2.9 7.4-7 9 c-4.1-1.6-7-4.7-7-9 V6 Z" {...common} />
          <path d="M9 11.8 L11.2 14 L15.2 9.6" {...common} />
        </>
      )}
      {kind === 'toggle' && (
        <>
          <rect x={3} y={8} width={18} height={8} rx={4} {...common} />
          <circle cx={15.5} cy={12} r={2.4} {...common} />
        </>
      )}
      {kind === 'mail' && (
        <>
          <rect x={3.5} y={5.5} width={17} height={13} rx={2} {...common} />
          <path d="M3.5 7.5 L12 13.5 L20.5 7.5" {...common} />
        </>
      )}
      {kind === 'box' && (
        <>
          <path d="M12 3 L20 7.5 V16.5 L12 21 L4 16.5 V7.5 Z" {...common} />
          <path d="M4 7.5 L12 12 L20 7.5 M12 12 V21" {...common} />
        </>
      )}
      {kind === 'sliders' && (
        <>
          <path d="M4 7 h16 M4 12 h16 M4 17 h16" {...common} />
          <circle cx={9.5} cy={7} r={2.1} {...common} fill="var(--color-fd-card)" />
          <circle cx={15.5} cy={12} r={2.1} {...common} fill="var(--color-fd-card)" />
          <circle cx={7.5} cy={17} r={2.1} {...common} fill="var(--color-fd-card)" />
        </>
      )}
    </svg>
  );
}

/** The seam label, stem, and plug prongs — stays put while the card unplugs. */
function Socket({ seam, muted }: { seam: string; muted?: boolean }) {
  const tone = muted ? 'var(--color-fd-muted-foreground)' : 'var(--accent-gen)';
  return (
    <div className="flex flex-col items-center">
      <span className="font-mono text-[0.7rem]" style={{ color: tone }}>
        {seam}
      </span>
      <span aria-hidden className="mt-1.5 h-3.5 w-px" style={{ background: tone, opacity: 0.6 }} />
      <span aria-hidden className="flex gap-1.5">
        <span className="h-2.5 w-[3px] rounded-b-[2px]" style={{ background: tone, opacity: 0.85 }} />
        <span className="h-2.5 w-[3px] rounded-b-[2px]" style={{ background: tone, opacity: 0.85 }} />
      </span>
    </div>
  );
}

export function Batteries() {
  return (
    <div>
      {/* the core rail, with its socket strip along the bottom edge */}
      <div className="relative rounded-[4px] border border-(--line) bg-fd-card px-6 py-5">
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
          decorator live in the core; the heavy dependency hangs off a socket.
        </p>
        <div
          aria-hidden
          className="absolute inset-x-5 -bottom-px h-[3px] rounded-full"
          style={{
            background:
              'repeating-linear-gradient(90deg, var(--accent-gen) 0 16px, transparent 16px 40px)',
            opacity: 0.55,
          }}
        />
      </div>

      {/* the batteries, hanging from their seams — hover one to unplug it */}
      <div className="mt-7 grid gap-x-5 gap-y-9 md:grid-cols-2 lg:grid-cols-3">
        {batteries.map((battery) => (
          <div key={battery.seam} className="group flex flex-col">
            <Socket seam={battery.seam} />
            <div className="flex grow flex-col rounded-[4px] border border-(--line) border-t-2 bg-fd-card p-5 transition-transform duration-300 ease-out group-hover:translate-y-1.5 motion-reduce:transition-none motion-reduce:group-hover:translate-y-0"
              style={{ borderTopColor: 'color-mix(in oklab, var(--accent-gen) 55%, transparent)' }}
            >
              <div className="flex items-center gap-3">
                <BatteryIcon kind={battery.icon} />
                <h3 className="font-display text-base font-semibold tracking-[-0.01em] text-fd-foreground">
                  {battery.name}
                </h3>
              </div>
              <p className="mt-3 grow text-sm leading-relaxed text-(--body)">{battery.body}</p>
              <p className="mt-4 border-t border-(--line-soft) pt-3 font-mono text-[0.7rem] text-fd-muted-foreground">
                {battery.packages}
              </p>
            </div>
          </div>
        ))}

        {/* the empty socket */}
        <div className="flex flex-col">
          <Socket seam="any seam" muted />
          <div className="flex grow flex-col justify-center rounded-[4px] border border-dashed border-(--line) p-5">
            <h3 className="font-display text-base font-semibold tracking-[-0.01em] text-fd-foreground">
              Your implementation
            </h3>
            <p className="mt-2 text-sm leading-relaxed text-(--body)">
              Implement the seam, register it, done — no handler, attribute, or generated line
              changes. Unplug ours, plug in yours.
            </p>
          </div>
        </div>
      </div>

      <p className="mt-5 text-center font-mono text-xs text-fd-muted-foreground">
        hover a battery to unplug it — that&apos;s the point
      </p>
    </div>
  );
}
