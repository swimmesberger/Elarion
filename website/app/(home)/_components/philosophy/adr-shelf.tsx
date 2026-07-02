import { githubUrl } from '@/lib/shared';

/**
 * "On the record", literally: the full ADR corpus as a shelf of spines, each
 * linking to its decision record on GitHub. The eight featured in the
 * opinions grid below stand taller and carry the brand iris. Pure HTML/CSS —
 * hover lifts a spine off the shelf.
 */

const ADRS: { n: string; slug: string }[] = [
  { n: '0001', slug: 'event-transaction-phase' },
  { n: '0002', slug: 'cross-module-communication' },
  { n: '0003', slug: 'decorator-attachment-predicates' },
  { n: '0004', slug: 'handler-result-caching' },
  { n: '0005', slug: 'cross-module-error-channel' },
  { n: '0006', slug: 'incremental-source-generator-conventions' },
  { n: '0007', slug: 'data-is-platform-module-as-plugin' },
  { n: '0008', slug: 'bounded-contexts-and-the-graduation-path' },
  { n: '0009', slug: 'authorization-building-blocks' },
  { n: '0010', slug: 'event-bus-is-pub-sub-only' },
  { n: '0011', slug: 'runtime-settings-subsystem' },
  { n: '0012', slug: 'dynamic-variable-references' },
  { n: '0013', slug: 'resource-and-data-level-authorization' },
  { n: '0014', slug: 'cross-assembly-generator-composition' },
  { n: '0015', slug: 'ef-core-transaction-participation' },
  { n: '0016', slug: 'feature-flag-gating' },
  { n: '0017', slug: 'dependency-light-core' },
  { n: '0018', slug: 'generated-infrastructure-is-framework-named' },
  { n: '0019', slug: 'variant-service-injection' },
  { n: '0020', slug: 'postgres-unlogged-l2-cache' },
  { n: '0021', slug: 'idempotency' },
  { n: '0022', slug: 'inbox-idempotent-event-consumers' },
  { n: '0023', slug: 'canonical-json-serialization' },
];

/** The eight stances featured in the opinions grid. */
const FEATURED = new Set(['0001', '0007', '0008', '0010', '0017', '0018', '0020', '0021']);

export function AdrShelf() {
  return (
    <div>
      <div className="flex items-end gap-[5px] overflow-x-auto border-b-2 border-(--line) pb-0">
        {ADRS.map((adr) => {
          const featured = FEATURED.has(adr.n);
          return (
            <a
              key={adr.n}
              href={`${githubUrl}/blob/main/docs/decisions/${adr.n}-${adr.slug}.md`}
              target="_blank"
              rel="noreferrer"
              title={`ADR-${adr.n} — ${adr.slug.replace(/-/g, ' ')}`}
              className={`flex w-7 shrink-0 items-start justify-center rounded-t-[3px] border border-b-0 pt-2 font-mono text-[0.62rem] transition-transform hover:-translate-y-1 motion-reduce:hover:translate-y-0 ${
                featured
                  ? 'h-16 border-(--accent-brand) bg-fd-card text-(--accent-brand)'
                  : 'h-11 border-(--line) text-fd-muted-foreground hover:text-fd-foreground'
              }`}
              style={featured ? { borderColor: 'color-mix(in oklab, var(--accent-brand) 55%, transparent)' } : undefined}
            >
              <span className="[writing-mode:vertical-rl]">{adr.n}</span>
            </a>
          );
        })}
      </div>
      <p className="mt-3 font-mono text-xs text-fd-muted-foreground">
        the full record, 0001 → 0023 — every spine opens the decision on GitHub. The tall ones are
        argued below.
      </p>
    </div>
  );
}
