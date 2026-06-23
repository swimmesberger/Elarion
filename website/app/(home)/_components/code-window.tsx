import { cn } from '@/lib/cn';
import type { ReactNode } from 'react';

/**
 * Always-dark editor chrome used to frame canonical Elarion code. Kept dark in
 * both themes on purpose — a terminal/editor surface reads as intentional and
 * sidesteps light-mode code-contrast issues.
 */
export function CodeWindow({
  filename,
  children,
  className,
  badge,
}: {
  filename: string;
  children: ReactNode;
  className?: string;
  badge?: string;
}) {
  return (
    <div
      className={cn(
        'overflow-hidden rounded-2xl border border-white/10 bg-ink-800 shadow-[0_40px_120px_-40px_rgba(46,104,255,0.55)]',
        className,
      )}
    >
      <div className="flex items-center gap-2 border-b border-white/8 bg-white/[0.03] px-4 py-3">
        <span className="size-3 rounded-full bg-[#ff5f57]/80" />
        <span className="size-3 rounded-full bg-[#febc2e]/80" />
        <span className="size-3 rounded-full bg-[#28c840]/80" />
        <span className="ml-2 font-mono text-xs text-white/45">{filename}</span>
        {badge ? (
          <span className="ml-auto rounded-md border border-white/10 bg-white/[0.04] px-2 py-0.5 font-mono text-[0.66rem] tracking-wide text-white/55">
            {badge}
          </span>
        ) : null}
      </div>
      <div className="code overflow-x-auto px-5 py-5">{children}</div>
    </div>
  );
}
