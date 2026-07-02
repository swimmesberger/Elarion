/**
 * "Move every failure to the left" — the moments a mistake can surface,
 * from keystroke to 3 a.m. Elarion's checks cluster in the cheap half;
 * the runtime-discovery alternative surfaces in the expensive half.
 */
export function FeedbackTimeline() {
  const stops = [
    { x: 110, label: 'as you type' },
    { x: 300, label: 'dotnet build' },
    { x: 480, label: 'CI' },
    { x: 660, label: 'deploy' },
    { x: 850, label: '3 a.m.' },
  ];

  const elarion = [
    { x: 110, w: 190, lines: ['cross-module reach', 'ELMOD002, in the IDE'] },
    { x: 300, w: 210, lines: ['missing wiring · no verb ·', 'auth that can’t fail closed'] },
    { x: 480, w: 200, lines: ['contract drift — the generated', 'client stops type-checking'] },
  ];

  const alternative = [
    { x: 660, w: 200, lines: ['missing registration —', 'found at startup'] },
    { x: 850, w: 190, lines: ['forgotten auth check —', 'found by an incident'] },
  ];

  return (
    <svg
      viewBox="0 0 960 400"
      className="h-auto w-full"
      role="img"
      aria-label="A timeline from typing to a 3 a.m. incident. With Elarion, cross-module reaches surface as you type, wiring and authorization mistakes fail the build, and contract drift fails CI. With runtime discovery, missing registrations surface at startup and forgotten authorization surfaces as a production incident."
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* cost zones */}
      <rect x={40} y={186} width={530} height={54} rx={6} fill="var(--color-fd-primary)" fillOpacity={0.09} />
      <rect x={578} y={186} width={342} height={54} rx={6} fill="#fb7185" fillOpacity={0.07} />
      <text x={56} y={234} fontSize={10.5} letterSpacing="0.14em" fill="var(--color-fd-primary)" fillOpacity={0.9}>
        CHEAP TO FIX
      </text>
      <text x={904} y={234} fontSize={10.5} letterSpacing="0.14em" textAnchor="end" fill="#fb7185" fillOpacity={0.85}>
        EXPENSIVE
      </text>

      {/* axis */}
      <line x1={40} y1={213} x2={920} y2={213} stroke="var(--line)" strokeWidth={1.5} />
      {stops.map((s) => (
        <g key={s.label}>
          <circle cx={s.x} cy={213} r={4.5} fill="var(--color-fd-background)" stroke="var(--color-fd-foreground)" strokeOpacity={0.55} strokeWidth={1.5} />
          <text x={s.x} y={266} fontSize={12.5} textAnchor="middle" fill="var(--color-fd-foreground)">
            {s.label}
          </text>
        </g>
      ))}

      {/* Elarion: caught early (above the line) */}
      <text x={40} y={44} fontSize={11} letterSpacing="0.16em" fill="var(--color-fd-muted-foreground)">
        WITH ELARION
      </text>
      {elarion.map((c) => (
        <g key={c.x} className="text-fd-primary">
          <rect x={c.x - c.w / 2} y={60} width={c.w} height={58} rx={6} fill="var(--color-fd-card)" stroke="currentColor" strokeOpacity={0.45} />
          {c.lines.map((line, i) => (
            <text key={line} x={c.x} y={84 + i * 18} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-foreground)">
              {line}
            </text>
          ))}
          <line x1={c.x} y1={118} x2={c.x} y2={206} stroke="currentColor" strokeOpacity={0.4} strokeDasharray="3 4" />
        </g>
      ))}

      {/* the alternative: found late (below the line) */}
      <text x={40} y={314} fontSize={11} letterSpacing="0.16em" fill="var(--color-fd-muted-foreground)">
        WITH RUNTIME DISCOVERY
      </text>
      {alternative.map((c) => (
        <g key={c.x}>
          <rect x={c.x - c.w / 2} y={326} width={c.w} height={58} rx={6} fill="none" stroke="#fb7185" strokeOpacity={0.45} strokeDasharray="6 5" />
          {c.lines.map((line, i) => (
            <text key={line} x={c.x} y={350 + i * 18} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
              {line}
            </text>
          ))}
          <line x1={c.x} y1={220} x2={c.x} y2={326} stroke="#fb7185" strokeOpacity={0.35} strokeDasharray="3 4" />
        </g>
      ))}
    </svg>
  );
}
