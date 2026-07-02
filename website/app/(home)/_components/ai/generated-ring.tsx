import type { CSSProperties } from 'react';

/**
 * "78% of the finished code writes itself" — a single ring stat, drawn in on
 * load. 78% = 3,539 generated / (3,539 + 1,030 handwritten) non-blank lines,
 * measured on the sample application's application assembly.
 */
export function GeneratedRing() {
  const r = 100;
  const c = 2 * Math.PI * r; // ≈ 628.3
  const share = 0.78;

  return (
    <div className="flex flex-col items-center gap-8 sm:flex-row sm:gap-10">
      <div className="relative w-56 shrink-0 sm:w-64">
        <svg
          viewBox="0 0 240 240"
          className="h-auto w-full"
          role="img"
          aria-label="78 percent of the finished application code is produced automatically by the build; 22 percent is written by people and their AI."
          xmlns="http://www.w3.org/2000/svg"
        >
          <circle cx={120} cy={120} r={r} fill="none" stroke="var(--line)" strokeWidth={13} />
          <circle
            cx={120}
            cy={120}
            r={r}
            fill="none"
            stroke="var(--accent-gen)"
            strokeWidth={13}
            strokeLinecap="round"
            strokeDasharray={c}
            strokeDashoffset={c * (1 - share)}
            transform="rotate(-90 120 120)"
            className="ring-draw"
            style={{ '--ring-circumference': c } as CSSProperties}
          />
        </svg>
        {/* HTML overlay so the percentage can count up with the ring draw */}
        <div aria-hidden className="absolute inset-0 flex flex-col items-center justify-center">
          <span className="count-78 font-display text-5xl font-semibold tracking-[-0.02em] text-fd-foreground" />
          <span className="mt-1 text-[13px] text-fd-muted-foreground">writes itself</span>
        </div>
      </div>

      <div className="min-w-0">
        <div className="space-y-4">
          <div className="flex items-start gap-3">
            <span aria-hidden className="mt-1.5 size-2.5 shrink-0 rounded-full bg-(--accent-gen)" />
            <p className="text-sm leading-relaxed text-(--body)">
              <span className="font-medium text-fd-foreground">≈3,500 lines produced by the build</span>
              {' '}— the wiring, security plumbing, and connections. Nobody writes them, nobody
              reviews them, and no AI ever bills you for reading them.
            </p>
          </div>
          <div className="flex items-start gap-3">
            <span aria-hidden className="mt-1.5 size-2.5 shrink-0 rounded-full border border-fd-muted-foreground/60" />
            <p className="text-sm leading-relaxed text-(--body)">
              <span className="font-medium text-fd-foreground">≈1,000 lines written by people and their AI</span>
              {' '}— the business decisions. The only part that costs money to create, review, and
              read back.
            </p>
          </div>
        </div>
        <p className="mt-5 border-t border-(--line-soft) pt-3 font-mono text-[0.68rem] leading-relaxed text-fd-muted-foreground">
          measured on the sample application that ships with Elarion — reproducible by your
          engineers with one command
        </p>
      </div>
    </div>
  );
}
