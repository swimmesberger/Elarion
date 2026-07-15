import { githubUrl } from '@/lib/shared';
import { adrs, latestAdrNumber } from '@/lib/adr-catalog';

/**
 * "On the record", literally: the full ADR corpus as a shelf of spines, each
 * linking to its decision record on GitHub. The eight featured in the
 * opinions grid below stand taller and carry the brand iris. Pure HTML/CSS —
 * hover lifts a spine off the shelf.
 */

/** The eight stances featured in the opinions grid. */
const FEATURED = new Set(['0001', '0007', '0008', '0010', '0017', '0018', '0020', '0021']);

export function AdrShelf() {
  return (
    <div>
      {/* pt-2 gives lifted spines headroom — the strip is a scroll container,
          so paint above its content box would otherwise be clipped on hover */}
      <div className="flex items-end gap-[5px] overflow-x-auto border-b-2 border-(--line) pt-2 pb-0">
        {adrs.map((adr) => {
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
        the full record, 0001 → {latestAdrNumber} — every spine opens the decision on GitHub. The tall ones are
        argued below.
      </p>
    </div>
  );
}
