import { JournalQA } from './journal-qa';

/**
 * An illustrative joined trace + audit view — one customer action in plain
 * English with timings, followed by an example of asking an AI integration a
 * question over exported evidence. Always-dark surface,
 * matching the site's tooling windows.
 */

const steps = [
  { label: 'identity & permission check', ms: '1 ms' },
  { label: 'the business rule runs', ms: '39 ms' },
  { label: 'saved to the database', ms: '11 ms' },
  { label: 'receipt queued for delivery', ms: '2 ms' },
];

export function RequestJournal() {
  return (
    <div className="overflow-hidden rounded-[4px] border border-white/12 bg-ink-800">
      <div className="flex items-center justify-between gap-4 border-b border-white/8 px-4 py-2.5">
        <span className="truncate font-mono text-xs text-white/55">
          illustrative trace + audit view
        </span>
        <span className="shrink-0 font-mono text-[0.66rem] uppercase tracking-[0.14em] text-white/35">
          illustrative
        </span>
      </div>

      <div className="px-4 py-4 font-mono text-[0.8rem] leading-relaxed">
        <div className="flex items-baseline justify-between gap-4">
          <span className="text-[#dfe7f6]">
            <span className="text-[#64749b]">14:03:22 </span>a customer creates an invoice
          </span>
          <span className="shrink-0 text-[#64749b]">
            48 ms <span className="text-[#5ad6ea]">✓</span>
          </span>
        </div>
        <div className="mt-1.5 space-y-1 border-l border-white/10 pl-4">
          {steps.map((step) => (
            <div key={step.label} className="flex items-baseline justify-between gap-4">
              <span className="text-[#b9c6e4]">{step.label}</span>
              <span className="shrink-0 text-[#64749b]">{step.ms}</span>
            </div>
          ))}
        </div>
      </div>

      <JournalQA />
    </div>
  );
}
