import { CodeWindow } from './code-window';

/**
 * Diffstat of the first production migration onto Elarion: the application
 * the framework was extracted from, swapping its home-grown foundation for
 * the released packages in one pull request. Real numbers from a private
 * repository, rendered as a git-style diffstat — the landing page's "real
 * artifacts as the only artwork" rule.
 */

const ADDED = 391;
const REMOVED = 16_223;

function DiffRow({
  label,
  value,
  color,
  share,
}: {
  label: string;
  value: string;
  color: string;
  share: number;
}) {
  return (
    <div className="grid grid-cols-[7rem_5.5rem_1fr] items-center gap-x-4 py-2 sm:grid-cols-[8rem_6rem_1fr]">
      <span className="text-[#8398bd]">{label}</span>
      <span className="text-right font-medium tabular-nums" style={{ color }}>
        {value}
      </span>
      <span className="relative block h-2.5 overflow-hidden rounded-[1px] bg-white/6">
        <span
          className="absolute inset-y-0 left-0 rounded-[1px]"
          style={{ width: `${share}%`, minWidth: 3, background: color }}
        />
      </span>
    </div>
  );
}

export function MigrationDiffStat({ className }: { className?: string }) {
  // The additions bar is drawn to true scale against the deletions bar —
  // its near-invisibility is the argument.
  const addedShare = (ADDED / REMOVED) * 100;
  return (
    <CodeWindow
      filename="git diff --shortstat home-grown..elarion"
      note="one pull request"
      className={className}
    >
      <div className="code min-w-[22rem]">
        <DiffRow
          label="insertions(+)"
          value={`+${ADDED.toLocaleString('en-US')}`}
          color="#3fb950"
          share={addedShare}
        />
        <DiffRow
          label="deletions(-)"
          value={`−${REMOVED.toLocaleString('en-US')}`}
          color="#f85149"
          share={100}
        />
        <div className="mt-3 border-t border-white/8 pt-3 text-xs text-[#64749b]">
          net −{(REMOVED - ADDED).toLocaleString('en-US')} lines ·{' '}
          {Math.round(REMOVED / ADDED)} deleted for every line added
        </div>
      </div>
    </CodeWindow>
  );
}
