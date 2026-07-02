/**
 * "Build once, reach everyone" — one feature radiating to the three audiences
 * (customers, partners, AI assistants), with a single permission checkpoint on
 * the shared path. Theme-safe: all colors come from Fumadocs CSS variables via
 * currentColor groups. Motion is a CSS dash-flow, disabled under
 * prefers-reduced-motion.
 */
export function AudienceDiagram() {
  const wires = [
    { d: 'M356,230 C470,230 500,85 620,85', delay: '0s' },
    { d: 'M356,230 C470,230 500,230 620,230', delay: '0.5s' },
    { d: 'M356,230 C470,230 500,375 620,375', delay: '1s' },
  ];

  const targets = [
    {
      y: 40,
      title: 'Your customers',
      sub: ['the website and apps they use', 'every day'],
      accent: false,
    },
    {
      y: 185,
      title: 'Your partners',
      sub: ['system-to-system connections', 'with other companies'],
      accent: false,
    },
    {
      y: 330,
      title: 'AI assistants & agents',
      sub: ['Claude · ChatGPT · Copilot — via MCP,', 'the standard the AI industry agreed on'],
      accent: true,
    },
  ];

  return (
    <svg
      viewBox="0 0 960 460"
      className="h-auto w-full"
      role="img"
      aria-label="One feature, built once, connects to three audiences — your customers, your partners, and AI assistants — through a single permission checkpoint that applies the same rules to all of them."
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* wires: quiet base + traveling pulse */}
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
      <path d="M296,230 L318,230" stroke="var(--line)" strokeWidth={1.5} />

      {/* source node */}
      <g>
        <rect
          x={36}
          y={172}
          width={260}
          height={116}
          rx={8}
          fill="var(--color-fd-card)"
          stroke="var(--line)"
        />
        <text x={64} y={222} fontSize={19} fontWeight={600} fill="var(--color-fd-foreground)">
          Your feature
        </text>
        <text x={64} y={248} fontSize={12.5} fill="var(--color-fd-muted-foreground)">
          built once — by your team
        </text>
        <text x={64} y={266} fontSize={12.5} fill="var(--color-fd-muted-foreground)">
          and its AI
        </text>
      </g>

      {/* the shared checkpoint: one padlock on the path everyone takes */}
      <g className="text-fd-primary">
        <circle cx={337} cy={230} r={19} fill="var(--color-fd-card)" stroke="currentColor" strokeOpacity={0.6} />
        <circle cx={337} cy={230} r={19} fill="none" stroke="currentColor" strokeOpacity={0.35} className="soft-pulse" strokeWidth={5} />
        <path
          d="M331,228 v-4 a6,6 0 0 1 12,0 v4"
          fill="none"
          stroke="currentColor"
          strokeWidth={1.8}
        />
        <rect x={329} y={228} width={16} height={11} rx={2.5} fill="currentColor" />
      </g>
      <text x={360} y={288} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
        one checkpoint,
      </text>
      <text x={360} y={304} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
        the same rules for all
      </text>

      {/* audience nodes */}
      {targets.map((t) => (
        <g key={t.title}>
          <rect
            x={620}
            y={t.y}
            width={310}
            height={90}
            rx={8}
            fill="var(--color-fd-card)"
            stroke={t.accent ? 'var(--color-fd-primary)' : 'var(--line)'}
            strokeOpacity={t.accent ? 0.7 : 1}
            strokeWidth={t.accent ? 1.5 : 1}
          />
          <text x={646} y={t.y + 36} fontSize={16.5} fontWeight={600} fill="var(--color-fd-foreground)">
            {t.title}
          </text>
          {t.sub.map((line, i) => (
            <text
              key={line}
              x={646}
              y={t.y + 58 + i * 17}
              fontSize={12.5}
              fill="var(--color-fd-muted-foreground)"
            >
              {line}
            </text>
          ))}
        </g>
      ))}
    </svg>
  );
}
