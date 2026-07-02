/**
 * "The same guarded path, every time" — the life of one request on a single
 * line: a person taps or an AI asks, both pass the same checkpoint, the rule
 * runs, the result is saved, the journal is written. Sits directly under the
 * /ai hero as the page's establishing shot.
 */
export function RequestPath() {
  const stations = [
    { x: 350, w: 185, title: 'your business rule' },
    { x: 585, w: 175, title: 'saved, safely' },
    { x: 810, w: 130, title: 'the journal' },
  ];

  const wires = [
    { d: 'M200,57 C240,57 240,112 262,112', delay: '0s' },
    { d: 'M200,167 C240,167 240,112 262,112', delay: '0.4s' },
    { d: 'M298,112 L350,112', delay: '0.8s' },
    { d: 'M535,112 L585,112', delay: '1.1s' },
    { d: 'M760,112 L810,112', delay: '1.4s' },
  ];

  return (
    <svg
      viewBox="0 0 960 224"
      className="h-auto w-full"
      role="img"
      aria-label="A customer taps, or an AI asks — both requests pass the same permission checkpoint, then the business rule runs, the result is saved, and a journal entry is written."
      xmlns="http://www.w3.org/2000/svg"
    >
      {wires.map((w) => (
        <g key={w.d} className="text-fd-primary">
          <path d={w.d} fill="none" stroke="var(--line)" strokeWidth={1.5} />
          <path
            d={w.d}
            fill="none"
            stroke="currentColor"
            strokeWidth={2.2}
            strokeLinecap="round"
            strokeOpacity={0.75}
            className="flow-dash"
            style={{ animationDelay: w.delay }}
          />
        </g>
      ))}

      {/* the two ways in */}
      {[
        { y: 30, title: 'a customer taps' },
        { y: 140, title: 'an AI asks' },
      ].map((entry) => (
        <g key={entry.title}>
          <rect x={20} y={entry.y} width={180} height={54} rx={8} fill="var(--color-fd-card)" stroke="var(--line)" />
          <text
            x={110}
            y={entry.y + 33}
            fontSize={14.5}
            fontWeight={600}
            textAnchor="middle"
            fill="var(--color-fd-foreground)"
          >
            {entry.title}
          </text>
        </g>
      ))}

      {/* the checkpoint both must pass */}
      <g className="text-fd-primary">
        <circle cx={280} cy={112} r={18} fill="var(--color-fd-card)" stroke="currentColor" strokeOpacity={0.6} />
        <circle cx={280} cy={112} r={18} fill="none" stroke="currentColor" strokeOpacity={0.35} strokeWidth={5} className="soft-pulse" />
        <path d="M274,110 v-4 a6,6 0 0 1 12,0 v4" fill="none" stroke="currentColor" strokeWidth={1.8} />
        <rect x={272} y={110} width={16} height={11} rx={2.5} fill="currentColor" />
      </g>
      <text x={280} y={158} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
        the checkpoint
      </text>

      {/* the stations */}
      {stations.map((s) => (
        <g key={s.title}>
          <rect x={s.x} y={85} width={s.w} height={54} rx={8} fill="var(--color-fd-card)" stroke="var(--line)" />
          <text
            x={s.x + s.w / 2}
            y={118}
            fontSize={14.5}
            fontWeight={600}
            textAnchor="middle"
            fill="var(--color-fd-foreground)"
          >
            {s.title}
          </text>
        </g>
      ))}
      <text x={875} y={158} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
        written automatically
      </text>
    </svg>
  );
}
