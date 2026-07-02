import { cn } from '@/lib/cn';
import type { ReactNode } from 'react';

/**
 * Always-dark editor surface used to frame canonical Elarion code. Kept dark
 * in both themes on purpose — it reads as tooling, and sidesteps light-mode
 * code-contrast issues. Deliberately plain: a file path, an optional note,
 * a hairline. No window chrome.
 */
export function CodeWindow({
  filename,
  children,
  className,
  note,
}: {
  filename: string;
  children: ReactNode;
  className?: string;
  note?: string;
}) {
  return (
    <div className={cn('overflow-hidden rounded-[4px] border border-white/12 bg-ink-800', className)}>
      <div className="flex items-center justify-between gap-4 border-b border-white/8 px-4 py-2.5">
        <span className="truncate font-mono text-xs text-white/55">{filename}</span>
        {note ? (
          <span className="shrink-0 font-mono text-[0.66rem] uppercase tracking-[0.14em] text-white/35">
            {note}
          </span>
        ) : null}
      </div>
      <div className="overflow-x-auto px-4 py-4">{children}</div>
    </div>
  );
}
