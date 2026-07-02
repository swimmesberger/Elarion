'use client';

import { useId, useState, type ReactNode } from 'react';
import { cn } from '@/lib/cn';

export type OutputTab = {
  /** File-path style label, e.g. `GetClientRegistration.g.cs` */
  label: string;
  /** One-line description shown above the pane */
  summary: string;
  panel: ReactNode;
};

/**
 * File-tab switcher for the "what the build emits" showcase. Panels are
 * server-rendered ReactNodes; this component only toggles visibility, so the
 * code highlighting ships zero client JS of its own.
 */
export function OutputTabs({ tabs, className }: { tabs: OutputTab[]; className?: string }) {
  const [active, setActive] = useState(0);
  const baseId = useId();

  return (
    <div className={cn('overflow-hidden rounded-[4px] border border-white/12 bg-ink-800', className)}>
      <div role="tablist" aria-label="Generated output" className="flex overflow-x-auto border-b border-white/8">
        {tabs.map((tab, i) => (
          <button
            key={tab.label}
            role="tab"
            id={`${baseId}-tab-${i}`}
            aria-selected={i === active}
            aria-controls={`${baseId}-panel-${i}`}
            onClick={() => setActive(i)}
            className={cn(
              'shrink-0 border-r border-white/8 px-4 py-2.5 font-mono text-xs transition-colors',
              i === active
                ? 'bg-white/[0.06] text-white/90'
                : 'text-white/40 hover:bg-white/[0.03] hover:text-white/70',
            )}
          >
            {tab.label}
          </button>
        ))}
      </div>
      {tabs.map((tab, i) => (
        <div
          key={tab.label}
          role="tabpanel"
          id={`${baseId}-panel-${i}`}
          aria-labelledby={`${baseId}-tab-${i}`}
          hidden={i !== active}
        >
          <p className="border-b border-white/8 px-4 py-2 font-mono text-[0.7rem] text-white/40">
            {tab.summary}
          </p>
          <div className="overflow-x-auto px-4 py-4">{tab.panel}</div>
        </div>
      ))}
    </div>
  );
}
