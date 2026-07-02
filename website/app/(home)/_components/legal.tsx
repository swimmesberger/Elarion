import type { ReactNode } from 'react';
import { PAD, Ticks } from './section';

/**
 * Quiet document shell for the legal pages (Impressum, Datenschutzerklärung).
 * Same rails as the rest of the site, deliberately plain typography.
 */
export function LegalShell({
  eyebrow,
  title,
  subtitle,
  updated,
  children,
}: {
  eyebrow: string;
  title: string;
  subtitle: string;
  updated: string;
  children: ReactNode;
}) {
  return (
    <main className="flex flex-1 flex-col overflow-x-clip">
      <div className="mx-auto w-full max-w-[80rem] border-x border-(--line)">
        <section className="relative border-b border-(--line)">
          <Ticks />
          <div className={`py-14 lg:py-16 ${PAD}`}>
            <p className="eyebrow">{eyebrow}</p>
            <h1 className="mt-5 font-display text-3xl font-semibold tracking-[-0.02em] text-fd-foreground sm:text-4xl">
              {title}
            </h1>
            <p className="mt-2 text-fd-muted-foreground">{subtitle}</p>

            <article className="mt-10 max-w-3xl">{children}</article>

            <p className="mt-12 border-t border-(--line-soft) pt-4 font-mono text-xs text-fd-muted-foreground">
              Stand / last updated: {updated}
            </p>
          </div>
        </section>
      </div>
    </main>
  );
}

export function LegalSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mt-8 border-t border-(--line) pt-5 first:mt-0 first:border-t-0 first:pt-0">
      <h2 className="font-display text-lg font-semibold tracking-[-0.01em] text-fd-foreground">
        {title}
      </h2>
      <div className="mt-3 space-y-3 text-sm leading-relaxed text-fd-muted-foreground">
        {children}
      </div>
    </section>
  );
}

export function DisclosureRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1 border-t border-(--line-soft) py-3 first:border-t-0 sm:flex-row sm:items-baseline">
      <dt className="w-56 shrink-0 font-mono text-xs text-fd-muted-foreground">{label}</dt>
      <dd className="text-sm text-fd-foreground">{children}</dd>
    </div>
  );
}
