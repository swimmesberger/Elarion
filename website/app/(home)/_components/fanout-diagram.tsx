import { cn } from '@/lib/cn';

const surfaces = [
  { label: 'Registered handler', sub: 'DI, no Program.cs entry', y: 68 },
  { label: 'JSON-RPC method', sub: 'POST /rpc', y: 172 },
  { label: 'MCP tool', sub: 'for AI agents', y: 276 },
  { label: 'HTTP endpoint', sub: 'REST + ProblemDetails', y: 380 },
  { label: 'TypeScript + Zod client', sub: 'schema-exported', y: 484 },
];

// Syntax palette (kept tight so colors read as intentional, not noisy).
const C = {
  attr: '#6fd6ff',
  string: '#9fe6a0',
  keyword: '#c79bff',
  ident: '#e9eef9',
  punct: '#8398bd',
  comment: '#5d6e94',
};

// One uniform monospace grid: every line shares the same font size and the same
// baseline step, so the block reads as real source rather than ragged text.
// Indents are expressed in character columns (not literal spaces) to dodge SVG
// whitespace collapsing.
const codeLines: { indent: number; segs: { t: string; c: string }[] }[] = [
  {
    indent: 0,
    segs: [
      { t: '[Handler(', c: C.attr },
      { t: '"clients.get"', c: C.string },
      { t: ')]', c: C.attr },
    ],
  },
  {
    indent: 0,
    segs: [
      { t: '[RequirePermission(', c: C.attr },
      { t: '"clients:read"', c: C.string },
      { t: ')]', c: C.attr },
    ],
  },
  {
    indent: 0,
    segs: [
      { t: 'sealed class ', c: C.keyword },
      { t: 'GetClient', c: C.ident },
    ],
  },
  {
    indent: 2,
    segs: [
      { t: ': ', c: C.punct },
      { t: 'IHandler', c: C.attr },
      { t: '<Query, Result<…>>', c: C.punct },
    ],
  },
  { indent: 0, segs: [] },
  { indent: 0, segs: [{ t: '// one class. one source of truth.', c: C.comment }] },
];

const CARD_X = 48;
const CARD_Y = 156;
const CARD_W = 360;
const CARD_H = 248;

const CODE_FONT = 13;
const CHAR_W = 7.8; // JetBrains Mono advance at CODE_FONT
const LINE_H = 27;
const FIRST_BASELINE = 250;
const GUTTER_NUM_X = CARD_X + 34;
const GUTTER_RULE_X = CARD_X + 46;
const CODE_X = CARD_X + 60;

const EMIT_X = CARD_X + CARD_W; // emit node sits on the card's right edge
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

      {/* source handler card, styled as a code-editor pane */}
      <rect
        x={CARD_X}
        y={CARD_Y}
        width={CARD_W}
        height={CARD_H}
        rx={18}
        fill="#0a1124"
        stroke="rgba(124,150,210,0.28)"
      />

      {/* window chrome: control dots + filename tab */}
      {['#6e45f6', '#2e68ff', '#2fd6e8'].map((fill, i) => (
        <circle key={fill} cx={CARD_X + 26 + i * 18} cy={CARD_Y + 21} r={4.5} fill={fill} fillOpacity={0.85} />
      ))}
      <text x={CARD_X + CARD_W - 22} y={CARD_Y + 26} fontSize={12} fill="#647499" textAnchor="end">
        GetClient.cs
      </text>
      <line
        x1={CARD_X}
        y1={CARD_Y + 42}
        x2={CARD_X + CARD_W}
        y2={CARD_Y + 42}
        stroke="rgba(124,150,210,0.18)"
        strokeWidth={1}
      />

      {/* line-number gutter rule */}
      <line
        x1={GUTTER_RULE_X}
        y1={FIRST_BASELINE - 18}
        x2={GUTTER_RULE_X}
        y2={FIRST_BASELINE + (codeLines.length - 1) * LINE_H + 8}
        stroke="rgba(124,150,210,0.14)"
        strokeWidth={1}
      />

      {/* code, on a uniform monospace grid */}
      {codeLines.map((line, i) => {
        const baseline = FIRST_BASELINE + i * LINE_H;
        return (
          <g key={i}>
            <text x={GUTTER_NUM_X} y={baseline} fontSize={11} fill="#43506e" textAnchor="end">
              {i + 1}
            </text>
            {line.segs.length > 0 && (
              <text x={CODE_X + line.indent * CHAR_W} y={baseline} fontSize={CODE_FONT}>
                {line.segs.map((seg, j) => (
                  <tspan key={j} fill={seg.c}>
                    {seg.t}
                  </tspan>
                ))}
              </text>
            )}
          </g>
        );
      })}

      {/* emit node — the brand pulse lives here, where the fan-out originates */}
      <circle
        cx={EMIT_X}
        cy={EMIT_Y}
        r={20}
        fill="none"
        stroke="url(#emit-grad)"
        strokeOpacity={0.4}
        className="node-pulse"
        style={{ transformBox: 'fill-box', transformOrigin: 'center' }}
      />
      <circle cx={EMIT_X} cy={EMIT_Y} r={11} fill="url(#emit-grad)" />

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

      <text x={CARD_X} y={446} fontSize={12.5} fill="#647499" letterSpacing="0.16em">
        SOURCE-GENERATED · AOT-SAFE · NO RUNTIME REFLECTION
      </text>
    </svg>
  );
}
