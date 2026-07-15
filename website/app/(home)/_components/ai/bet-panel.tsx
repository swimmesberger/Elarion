import { adrCount } from '@/lib/adr-catalog';

/**
 * "The bet you're actually making" — drawn as an I/O panel: your product is a
 * chassis whose every external connection is a standard port (nothing
 * proprietary), with an exit door standing open in the wall and the
 * inspection seals inside. Theme-safe via CSS variables; cables animate with
 * the shared dash-flow.
 */
export function BetPanel() {
  const ports = [
    { x: 130, name: 'MCP', sub: 'compatible AI clients' },
    { x: 270, name: 'OpenTelemetry', sub: 'any monitoring' },
    { x: 410, name: 'OpenFeature', sub: 'any flag provider' },
    { x: 550, name: 'HTTP · JSON-RPC', sub: 'any client' },
  ];

  // Three seals of 192px inside the 640px chassis (x 40..680): 16px margins.
  const seals = [
    { x: 56, main: 'APACHE-2.0', sub: 'yours to keep' },
    { x: 264, main: `${adrCount} ADRs ON FILE`, sub: 'every tradeoff reasoned' },
    { x: 472, main: '1,500+ TESTS', sub: 'guarding the promises' },
  ];

  return (
    <svg
      viewBox="0 0 960 410"
      className="h-auto w-full"
      role="img"
      aria-label={`Your product drawn as a chassis. Every external connection is a standard port — MCP for compatible AI clients, OpenTelemetry for monitoring, OpenFeature for feature-flag providers, and HTTP or JSON-RPC for clients. An exit door stands open in the wall: leave anytime and keep a conventional .NET codebase. Inside, three seals: Apache-2.0, ${adrCount} decision records, more than fifteen hundred tests.`}
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* cables into the standard ports */}
      {ports.map((p, i) => (
        <g key={p.name}>
          <text x={p.x} y={22} fontSize={12.5} fontWeight={600} textAnchor="middle" fill="var(--color-fd-foreground)">
            {p.name}
          </text>
          <text x={p.x} y={40} fontSize={10.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
            {p.sub}
          </text>
          <g className="text-fd-primary">
            <path d={`M${p.x},50 L${p.x},76`} stroke="var(--line)" strokeWidth={1.5} fill="none" />
            <path
              d={`M${p.x},50 L${p.x},76`}
              stroke="currentColor"
              strokeWidth={2.2}
              strokeOpacity={0.75}
              strokeLinecap="round"
              fill="none"
              className="flow-dash"
              style={{ animationDelay: `${i * 0.35}s` }}
            />
          </g>
        </g>
      ))}

      {/* the chassis */}
      <rect x={40} y={80} width={640} height={250} rx={10} fill="var(--color-fd-card)" stroke="var(--color-fd-foreground)" strokeOpacity={0.35} strokeWidth={1.5} />
      <text x={64} y={112} fontSize={11} letterSpacing="0.14em" fill="var(--color-fd-muted-foreground)">
        YOUR PRODUCT
      </text>
      <text x={64} y={130} fontSize={10.5} fill="var(--color-fd-muted-foreground)">
        plain, portable .NET — nothing proprietary inside
      </text>

      {/* the ports, set into the top edge */}
      {ports.map((p) => (
        <rect
          key={p.name}
          x={p.x - 13}
          y={75}
          width={26}
          height={10}
          rx={2}
          fill="var(--color-fd-background)"
          stroke="var(--color-fd-primary)"
          strokeOpacity={0.7}
        />
      ))}

      {/* the exit door, standing open */}
      <rect x={676} y={160} width={8} height={75} fill="var(--color-fd-background)" />
      <path d="M680,160 A75,75 0 0 1 723,174" fill="none" stroke="var(--line)" strokeWidth={1} strokeDasharray="3 4" />
      <line x1={680} y1={235} x2={723} y2={174} stroke="var(--color-fd-foreground)" strokeOpacity={0.5} strokeWidth={2} strokeLinecap="round" />
      <g className="text-fd-primary">
        <path d="M690,200 L742,200" stroke="currentColor" strokeWidth={2} strokeOpacity={0.75} strokeLinecap="round" className="flow-dash" />
        <path d="M736,194 L744,200 L736,206" fill="none" stroke="currentColor" strokeWidth={2} strokeOpacity={0.75} strokeLinecap="round" strokeLinejoin="round" />
      </g>
      <text x={754} y={196} fontSize={13} fontWeight={600} fill="var(--color-fd-foreground)">
        the exit stays open
      </text>
      <text x={754} y={216} fontSize={10.8} fill="var(--color-fd-muted-foreground)">
        leave anytime — you keep a
      </text>
      <text x={754} y={232} fontSize={10.8} fill="var(--color-fd-muted-foreground)">
        conventional .NET codebase
      </text>

      {/* the inspection seals */}
      {seals.map((seal) => (
        <g key={seal.main}>
          <rect x={seal.x} y={262} width={192} height={52} rx={6} fill="none" stroke="var(--accent-brand)" strokeOpacity={0.5} />
          <rect x={seal.x + 4} y={266} width={184} height={44} rx={4} fill="none" stroke="var(--line-soft)" strokeDasharray="4 4" />
          <text x={seal.x + 96} y={284} fontSize={12.5} letterSpacing="0.08em" textAnchor="middle" fill="var(--color-fd-foreground)" style={{ fontFamily: 'var(--font-mono)' }}>
            {seal.main}
          </text>
          <text x={seal.x + 96} y={302} fontSize={10.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
            {seal.sub}
          </text>
        </g>
      ))}
    </svg>
  );
}
