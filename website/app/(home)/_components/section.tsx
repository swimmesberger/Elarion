import type { ReactNode } from 'react';

/**
 * Shared structure for the marketing pages ("engineering drawing" system):
 * hairline-railed sections with drafting ticks and numbered mono labels.
 */

export const PAD = 'px-5 sm:px-8 lg:px-12';

export function Ticks() {
  return (
    <>
      <span aria-hidden className="tick hidden lg:block" style={{ left: -8, top: -8 }} />
      <span aria-hidden className="tick hidden lg:block" style={{ right: -8, top: -8 }} />
    </>
  );
}

export function Section({
  id,
  n,
  label,
  aside,
  tinted,
  children,
}: {
  id: string;
  n: string;
  label: string;
  aside?: string;
  /** Subtle background wash — used on alternating sections for page rhythm. */
  tinted?: boolean;
  children: ReactNode;
}) {
  return (
    <section id={id} className={`relative border-b border-(--line) ${tinted ? 'bg-fd-muted/25' : ''}`}>
      <Ticks />
      <div className={`flex items-baseline justify-between gap-4 border-b border-(--line-soft) py-3 ${PAD}`}>
        <span className="eyebrow">
          <span className="text-(--accent-brand)">{n} /</span> {label}
        </span>
        {aside ? <span className="eyebrow hidden text-right sm:block">{aside}</span> : null}
      </div>
      {children}
    </section>
  );
}

export function SectionTitle({
  title,
  lead,
  points,
}: {
  title: ReactNode;
  lead?: ReactNode;
  /**
   * Scannable key fragments rendered beside the lead (editorial standfirst
   * pattern). Keep the lead to ~2 lines and let these carry the detail —
   * long intro paragraphs are walls nobody reads.
   */
  points?: ReactNode[];
}) {
  const heading = (
    <div className={points ? 'max-w-2xl' : 'max-w-3xl'}>
      <h2 className="font-display text-3xl font-semibold tracking-[-0.02em] text-fd-foreground sm:text-4xl">
        {title}
      </h2>
      {lead ? <p className="mt-4 text-[1.0625rem] leading-relaxed text-(--body)">{lead}</p> : null}
    </div>
  );

  if (!points) return heading;

  return (
    <div className="grid gap-x-14 gap-y-7 lg:grid-cols-[1.1fr_0.9fr] lg:items-start">
      {heading}
      <ul className="space-y-3 border-t border-(--line) pt-5 lg:border-t-0 lg:pt-2.5">
        {points.map((point, i) => (
          <li key={i} className="flex items-start gap-3 text-sm leading-relaxed text-(--body)">
            <span aria-hidden className="mt-[7px] size-1.5 shrink-0 bg-(--accent-gen)" />
            <span>{point}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function Mono({ children }: { children: ReactNode }) {
  return <span className="font-mono text-[0.92em] text-fd-foreground">{children}</span>;
}

export function GithubIcon({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="currentColor"
      aria-hidden
      className={className}
      xmlns="http://www.w3.org/2000/svg"
    >
      <path d="M12 .5C5.73.5.5 5.73.5 12.04c0 5.1 3.29 9.42 7.86 10.95.58.11.79-.25.79-.56 0-.28-.01-1.02-.02-2-3.2.7-3.88-1.55-3.88-1.55-.52-1.34-1.28-1.69-1.28-1.69-1.05-.72.08-.71.08-.71 1.16.08 1.77 1.2 1.77 1.2 1.03 1.78 2.7 1.26 3.36.97.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.28 1.19-3.09-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.18 1.18a11 11 0 0 1 2.9-.39c.98 0 1.97.13 2.9.39 2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.76.11 3.05.74.81 1.19 1.83 1.19 3.09 0 4.42-2.69 5.39-5.25 5.68.41.36.78 1.05.78 2.12 0 1.53-.01 2.77-.01 3.15 0 .31.21.68.8.56A11.55 11.55 0 0 0 23.5 12.04C23.5 5.73 18.27.5 12 .5Z" />
    </svg>
  );
}
