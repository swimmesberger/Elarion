'use client';

import { useState } from 'react';
import { cn } from '@/lib/cn';

/**
 * The interactive payoff of the journal: pick a question a leader would
 * actually ask, read the answer the AI gives from the journal. Illustrative
 * by design and labeled as such in the parent window.
 */

const EXCHANGES = [
  {
    q: 'Why did invoicing feel slow yesterday afternoon?',
    a: 'Between 3 and 4 p.m., database saves took nine times longer than usual — they overlapped with the daily backup. Moving the backup to 2 a.m. removes the slowdown.',
  },
  {
    q: 'Did anyone outside finance touch payroll data?',
    a: 'No. Two attempts last week were stopped at the checkpoint — an AI assistant acting for a sales user without that permission. Nothing was read, and both attempts are on record.',
  },
  {
    q: 'What should we speed up first?',
    a: 'Report exports — the slowest customer-facing action at 1.8 seconds, most of it one repeated calculation. Caching that result is a small, low-risk change; the numbers behind it update hourly at most.',
  },
];

export function JournalQA() {
  const [active, setActive] = useState(0);

  return (
    <div className="border-t border-white/8 bg-white/[0.02] px-4 py-4">
      <p className="font-mono text-[0.66rem] uppercase tracking-[0.14em] text-white/35">
        You ask — pick a question
      </p>
      <div className="mt-2 flex flex-col items-start gap-1.5">
        {EXCHANGES.map((exchange, i) => (
          <button
            key={exchange.q}
            type="button"
            onClick={() => setActive(i)}
            aria-pressed={i === active}
            className={cn(
              'rounded-[4px] border px-3 py-1.5 text-left text-[0.85rem] italic leading-snug transition-colors',
              i === active
                ? 'border-white/25 bg-white/[0.06] text-[#dfe7f6]'
                : 'border-white/10 text-white/45 hover:border-white/20 hover:text-white/70',
            )}
          >
            “{exchange.q}”
          </button>
        ))}
      </div>

      <p className="mt-4 font-mono text-[0.66rem] uppercase tracking-[0.14em] text-white/35">
        Your AI answers, from the journal
      </p>
      <p className="mt-1.5 min-h-20 text-[0.92rem] leading-relaxed text-[#b9c6e4]">
        {EXCHANGES[active].a}
        <span className="caret" />
      </p>
    </div>
  );
}
