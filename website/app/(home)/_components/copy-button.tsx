'use client';

import { Check, Copy } from 'lucide-react';
import { useState } from 'react';
import { cn } from '@/lib/cn';

export function CopyCommand({ command, className }: { command: string; className?: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
    } catch {
      // clipboard unavailable — no-op
    }
  }

  return (
    <button
      type="button"
      onClick={copy}
      aria-label="Copy command"
      className={cn(
        'group flex w-full items-center justify-between gap-3 rounded-[4px] border border-(--line) bg-fd-card px-4 py-3 text-left font-mono text-[0.82rem] transition-colors hover:border-fd-primary/50',
        className,
      )}
    >
      <span className="flex items-center gap-2.5 overflow-x-auto whitespace-nowrap">
        <span className="select-none text-fd-primary">$</span>
        <span className="text-fd-foreground">{command}</span>
      </span>
      <span className="shrink-0 text-fd-muted-foreground transition-colors group-hover:text-fd-foreground">
        {copied ? <Check className="size-4 text-fd-primary" /> : <Copy className="size-4" />}
      </span>
    </button>
  );
}
