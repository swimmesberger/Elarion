/**
 * The decorator pipeline — the architecture centerfold. Station order matches
 * the generated BuildPipeline construction order (tracing outermost, caching
 * innermost). Attribute tags sit above the station they control; stations
 * attach only when asked for.
 */
export function PipelineDiagram() {
  const stations = [
    { label: 'tracing', tag: 'always on', tagRow: 0 },
    { label: 'authorization', tag: '[RequirePermission]', tagRow: 1 },
    { label: 'feature gate', tag: '[FeatureGate]', tagRow: 0 },
    { label: 'resilience', tag: '[Resilient]', tagRow: 1 },
    { label: 'transaction', tag: 'ICommand → auto', tagRow: 0 },
    { label: 'validation', tag: 'validator → auto', tagRow: 1 },
    { label: 'caching', tag: '[Cacheable]', tagRow: 0 },
  ];

  const STATION_W = 86;
  const GAP = 10;
  const X0 = 154;
  const Y = 150;

  return (
    <svg
      viewBox="0 0 960 330"
      className="h-auto w-full"
      role="img"
      aria-label="Every transport — JSON-RPC, REST, MCP, scheduled jobs, and events — enters one pipeline: tracing, authorization, feature gate, resilience, transaction, validation, caching, then your handler. After the handler, domain events dispatch inline in the same transaction and integration events deliver after commit. Each stage attaches only when the handler asks for it."
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* the transports enter as one arrow */}
      <text x={30} y={132} fontSize={11} letterSpacing="0.1em" fill="var(--color-fd-muted-foreground)">
        JSON-RPC
      </text>
      <text x={30} y={150} fontSize={11} letterSpacing="0.1em" fill="var(--color-fd-muted-foreground)">
        REST · MCP
      </text>
      <text x={30} y={168} fontSize={11} letterSpacing="0.1em" fill="var(--color-fd-muted-foreground)">
        jobs · events
      </text>
      <text x={30} y={192} fontSize={11} letterSpacing="0.1em" fill="var(--color-fd-primary)">
        one pipeline
      </text>
      <g className="text-fd-primary">
        <path d={`M118,${Y + 28} L${X0 - 6},${Y + 28}`} stroke="var(--line)" strokeWidth={1.5} fill="none" />
        <path
          d={`M118,${Y + 28} L${X0 - 6},${Y + 28}`}
          stroke="currentColor"
          strokeWidth={2.2}
          strokeOpacity={0.75}
          strokeLinecap="round"
          fill="none"
          className="flow-dash"
        />
      </g>

      {/* stations */}
      {stations.map((s, i) => {
        const x = X0 + i * (STATION_W + GAP);
        const tagY = s.tagRow === 0 ? 74 : 104;
        return (
          <g key={s.label}>
            {/* attribute tag with leader tick */}
            <text x={x + STATION_W / 2} y={tagY} fontSize={10.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)" style={{ fontFamily: 'var(--font-mono)' }}>
              {s.tag}
            </text>
            <line x1={x + STATION_W / 2} y1={tagY + 6} x2={x + STATION_W / 2} y2={Y - 4} stroke="var(--line-soft)" strokeWidth={1} />
            <rect x={x} y={Y} width={STATION_W} height={56} rx={5} fill="var(--color-fd-muted)" stroke="var(--line)" />
            <text x={x + STATION_W / 2} y={Y + 33} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-foreground)">
              {s.label}
            </text>
            {/* connector to the next station */}
            {i < stations.length ? (
              <line x1={x + STATION_W} y1={Y + 28} x2={x + STATION_W + GAP} y2={Y + 28} stroke="var(--line)" strokeWidth={1.5} />
            ) : null}
          </g>
        );
      })}

      {/* your handler — the point of it all */}
      {(() => {
        const x = X0 + stations.length * (STATION_W + GAP);
        return (
          <g>
            <rect x={x} y={Y - 6} width={128} height={68} rx={6} fill="var(--color-fd-card)" stroke="var(--color-fd-primary)" strokeWidth={1.5} strokeOpacity={0.8} />
            <text x={x + 64} y={Y + 24} fontSize={13} fontWeight={600} textAnchor="middle" fill="var(--color-fd-foreground)">
              your handler
            </text>
            <text x={x + 64} y={Y + 43} fontSize={10.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
              business logic only
            </text>

            {/* what leaves the handler: the two event planes */}
            <path d={`M${x + 40},${Y + 62} C${x + 40},${Y + 108} ${x - 100},${Y + 108} ${x - 150},${Y + 108}`} fill="none" stroke="var(--line)" strokeWidth={1.5} />
            <text x={x - 158} y={Y + 112} fontSize={11} textAnchor="end" fill="var(--color-fd-muted-foreground)">
              domain events — inline, same transaction
            </text>
            <path d={`M${x + 88},${Y + 62} C${x + 88},${Y + 138} ${x - 100},${Y + 138} ${x - 150},${Y + 138}`} fill="none" stroke="var(--line)" strokeWidth={1.5} />
            <text x={x - 158} y={Y + 142} fontSize={11} textAnchor="end" fill="var(--color-fd-muted-foreground)">
              integration events — after commit, via the outbox
            </text>
          </g>
        );
      })()}
    </svg>
  );
}
