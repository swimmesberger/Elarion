/**
 * "Mistakes stop at the gate" — changes flow toward a checkpoint; sound ones
 * pass and ship, the risky one is turned back with instructions. Drawn with
 * theme variables; the only motion is dash-flow along the arrows.
 */
export function GateDiagram() {
  const incoming = [
    { y: 46, title: 'New feature', sub: 'written by AI in minutes', ok: true },
    { y: 126, title: 'Improvement', sub: 'written by AI in minutes', ok: true },
    { y: 206, title: 'Risky change', sub: 'would quietly weaken a security rule', ok: false },
  ];

  const shipped = [
    { y: 66, title: 'New feature' },
    { y: 166, title: 'Improvement' },
  ];

  return (
    <svg
      viewBox="0 0 960 360"
      className="h-auto w-full"
      role="img"
      aria-label="Three changes approach the framework's gate of automatic checks. The two sound ones pass and ship. The risky one is rejected and returned with written instructions, which the AI uses to fix it and resubmit."
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* forward arrows into the gate */}
      {incoming.map((c) => (
        <g key={c.title} className={c.ok ? 'text-fd-primary' : 'text-[#fb7185]'}>
          <path d={`M320,${c.y + 32} L466,${c.y + 32}`} stroke="var(--line)" strokeWidth={1.5} fill="none" />
          <path
            d={`M320,${c.y + 32} L466,${c.y + 32}`}
            stroke="currentColor"
            strokeWidth={2.2}
            strokeOpacity={0.7}
            strokeLinecap="round"
            fill="none"
            className="flow-dash"
            style={{ animationDelay: `${c.y * 3}ms` }}
          />
        </g>
      ))}

      {/* arrows out of the gate for the shipped changes */}
      {shipped.map((c, i) => (
        <g key={c.title} className="text-fd-primary">
          <path d={`M494,${c.y + 30} L644,${c.y + 30}`} stroke="var(--line)" strokeWidth={1.5} fill="none" />
          <path
            d={`M494,${c.y + 30} L644,${c.y + 30}`}
            stroke="currentColor"
            strokeWidth={2.2}
            strokeOpacity={0.7}
            strokeLinecap="round"
            fill="none"
            className="flow-dash"
            style={{ animationDelay: `${i * 400}ms` }}
          />
        </g>
      ))}

      {/* the return loop: deflected at the gate (shares the forward line's
          endpoint) and curved back up into the risky card's bottom edge. The
          curve approaches vertically so the arrowhead reads as one motion. */}
      <g className="text-[#fb7185]">
        <path
          d="M466,238 C438,312 180,322 180,281"
          fill="none"
          stroke="currentColor"
          strokeWidth={2}
          strokeOpacity={0.7}
          strokeLinecap="round"
          className="flow-dash"
        />
        {/* filled arrowhead pointing up into the risky card's bottom edge */}
        <path d="M180,268 L173,282 L187,282 Z" fill="currentColor" fillOpacity={0.8} />
      </g>
      <text x={300} y={346} fontSize={12.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
        returned with written instructions — the AI fixes it and resubmits in seconds
      </text>

      {/* the gate */}
      <g opacity={0.16}>
        <rect x={468} y={30} width={26} height={250} rx={13} fill="var(--color-fd-primary)" className="soft-pulse" />
      </g>
      <rect x={477} y={36} width={8} height={238} rx={4} fill="var(--color-fd-primary)" />
      <text x={481} y={20} fontSize={12} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
        the gate — 60+ automatic checks
      </text>

      {/* incoming cards */}
      {incoming.map((c) => (
        <g key={c.title}>
          <rect
            x={40}
            y={c.y}
            width={280}
            height={64}
            rx={8}
            fill="var(--color-fd-card)"
            stroke={c.ok ? 'var(--line)' : '#fb7185'}
            strokeOpacity={c.ok ? 1 : 0.55}
          />
          <text x={62} y={c.y + 28} fontSize={14.5} fontWeight={600} fill="var(--color-fd-foreground)">
            {c.title}
          </text>
          <text x={62} y={c.y + 48} fontSize={12} fill="var(--color-fd-muted-foreground)">
            {c.sub}
          </text>
        </g>
      ))}

      {/* shipped cards */}
      {shipped.map((c) => (
        <g key={c.title}>
          <rect x={644} y={c.y} width={280} height={60} rx={8} fill="var(--color-fd-card)" stroke="var(--line)" />
          <text x={666} y={c.y + 27} fontSize={14.5} fontWeight={600} fill="var(--color-fd-foreground)">
            {c.title}
          </text>
          <text x={666} y={c.y + 46} fontSize={12} fill="var(--color-fd-muted-foreground)">
            checked · shipped to customers
          </text>
          <text x={896} y={c.y + 37} fontSize={16} textAnchor="end" fill="var(--color-fd-primary)">
            ✓
          </text>
        </g>
      ))}
    </svg>
  );
}
