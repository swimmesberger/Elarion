import { cn } from '@/lib/cn';

const surfaces = [
  { label: 'Registered service', sub: 'DI, no Program.cs entry', y: 68 },
  { label: 'JSON-RPC method', sub: 'POST /rpc', y: 172 },
  { label: 'MCP tool', sub: 'for AI agents', y: 276 },
  { label: 'HTTP endpoint', sub: 'REST + ProblemDetails', y: 380 },
  { label: 'TypeScript + Zod client', sub: 'schema-exported', y: 484 },
];

const EMIT_X = 408;
const EMIT_Y = 280;
const PILL_X = 636;

/**
 * "One annotated handler → every surface" — the core Elarion promise, drawn as a
 * compile-time fan-out. Self-contained SVG (no measured DOM connectors) so it
 * scales cleanly and animates with pure CSS dash-flow.
 */
export function FanoutDiagram({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 1000 560"
      className={cn('h-auto w-full', className)}
      role="img"
      aria-label="A single annotated handler is generated into a registered service, a JSON-RPC method, an MCP tool, an HTTP endpoint, and a typed TypeScript client at compile time."
      xmlns="http://www.w3.org/2000/svg"
      fontFamily="var(--font-mono)"
    >
      <defs>
        <linearGradient id="wire-grad" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stopColor="#6e45f6" />
          <stop offset="55%" stopColor="#2e68ff" />
          <stop offset="100%" stopColor="#2fd6e8" />
        </linearGradient>
        <linearGradient id="emit-grad" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stopColor="#6e45f6" />
          <stop offset="52%" stopColor="#2e68ff" />
          <stop offset="100%" stopColor="#2fd6e8" />
        </linearGradient>
      </defs>

      {/* connector wires */}
      {surfaces.map((s, i) => (
        <g key={s.label}>
          <path
            d={`M${EMIT_X},${EMIT_Y} C530,${EMIT_Y} 540,${s.y + 32} ${PILL_X},${s.y + 32}`}
            fill="none"
            stroke="url(#wire-grad)"
            strokeWidth={2}
            strokeOpacity={0.32}
          />
          <path
            d={`M${EMIT_X},${EMIT_Y} C530,${EMIT_Y} 540,${s.y + 32} ${PILL_X},${s.y + 32}`}
            fill="none"
            stroke="url(#wire-grad)"
            strokeWidth={2.4}
            strokeLinecap="round"
            className="wire-flow"
            style={{ animationDelay: `${i * 0.22}s` }}
          />
        </g>
      ))}

      {/* source handler card */}
      <rect
        x={48}
        y={170}
        width={360}
        height={220}
        rx={18}
        fill="#0a1124"
        stroke="rgba(124,150,210,0.28)"
      />
      {/* brand node motif on the card edge */}
      {[212, 280, 348].map((cy, i) => (
        <circle
          key={cy}
          cx={78}
          cy={cy}
          r={9}
          fill="url(#emit-grad)"
          className="node-pulse"
          style={{ animationDelay: `${i * 0.5}s`, opacity: 0.92 - i * 0.08 }}
        />
      ))}
      <text x={110} y={210} fontSize={15} fill="#6fd6ff">
        [RpcMethod(
        <tspan fill="#9fe6a0">&quot;clients.get&quot;</tspan>)]
      </text>
      <text x={110} y={262} fontSize={15} fill="#c79bff">
        sealed class{' '}
        <tspan fill="#e9eef9">GetClient</tspan>
      </text>
      <text x={110} y={300} fontSize={13.5} fill="#8398bd">
        : IHandler&lt;Query, Result&lt;…&gt;&gt;
      </text>
      <text x={110} y={352} fontSize={12.5} fill="#647499">
        // one class. one source of truth.
      </text>

      {/* emit node */}
      <circle cx={EMIT_X} cy={EMIT_Y} r={11} fill="url(#emit-grad)" />
      <circle cx={EMIT_X} cy={EMIT_Y} r={20} fill="none" stroke="url(#emit-grad)" strokeOpacity={0.4} />

      {/* surface pills */}
      {surfaces.map((s) => (
        <g key={s.label}>
          <rect
            x={PILL_X}
            y={s.y}
            width={316}
            height={64}
            rx={14}
            fill="#0c1428"
            stroke="rgba(124,150,210,0.25)"
          />
          <rect x={PILL_X} y={s.y} width={4} height={64} rx={2} fill="url(#emit-grad)" />
          <text x={PILL_X + 24} y={s.y + 28} fontSize={16} fill="#e9eef9">
            {s.label}
          </text>
          <text x={PILL_X + 24} y={s.y + 48} fontSize={12.5} fill="#8398bd">
            {s.sub}
          </text>
        </g>
      ))}

      <text x={48} y={430} fontSize={12.5} fill="#647499" letterSpacing="0.16em">
        SOURCE-GENERATED · AOT-SAFE · NO RUNTIME REFLECTION
      </text>
    </svg>
  );
}
