/**
 * The maxim, drawn: application patterns are detected from what the code
 * already states; platform capabilities are declared once, explicitly.
 * The third option — guessing at runtime — is struck out. Theme-safe SVG,
 * dev-level labels on purpose (this page's audience reads attributes).
 */
export function MaximSplit() {
  const detected = [
    'class GetClient : IHandler<Query, Result<…>>',
    'class OrderValidator : AbstractValidator<…>',
    '[ConsumeEvent] OnOrderShipped : IHandler<…>',
    '[EntityConfiguration] InvoiceConfiguration',
  ];

  const declared = [
    '[Cacheable(Duration = "00:05:00")]',
    '[Resilient("external-api")]',
    '[Idempotent]',
    '[RequirePermission("invoices", "write")]',
  ];

  const chip = (x: number, y: number, text: string, key: string) => (
    <g key={key}>
      <rect x={x} y={y} width={392} height={42} rx={5} fill="var(--color-fd-muted)" stroke="var(--line-soft)" />
      <text
        x={x + 18}
        y={y + 26}
        fontSize={12.5}
        fill="var(--color-fd-foreground)"
        style={{ fontFamily: 'var(--font-mono)' }}
      >
        {text}
      </text>
    </g>
  );

  return (
    <svg
      viewBox="0 0 960 470"
      className="h-auto w-full"
      role="img"
      aria-label="Application patterns — handlers, validators, event consumers, entity configurations — are detected at build time and their wiring is generated. Platform capabilities — caching, resilience, idempotency, authorization — are declared once as attributes and attached as decorators. Runtime scanning, convention guessing, and reflection magic are explicitly ruled out."
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* left: detected */}
      <rect x={24} y={24} width={440} height={344} rx={8} fill="none" stroke="var(--line)" />
      <text x={48} y={58} fontSize={11} letterSpacing="0.16em" fill="var(--color-fd-muted-foreground)">
        YOUR CODE ALREADY SAYS IT
      </text>
      {detected.map((t, i) => chip(48, 76 + i * 54, t, `d${i}`))}
      <g className="text-fd-primary">
        <path d="M244,292 L244,312" stroke="currentColor" strokeWidth={2} strokeOpacity={0.7} className="flow-dash" />
      </g>
      <rect x={48} y={314} width={392} height={38} rx={5} fill="var(--color-fd-primary)" fillOpacity={0.1} stroke="var(--color-fd-primary)" strokeOpacity={0.45} />
      <text x={244} y={338} fontSize={12.5} textAnchor="middle" fill="var(--color-fd-foreground)">
        detected at build — the wiring is generated
      </text>

      {/* right: declared */}
      <rect x={496} y={24} width={440} height={344} rx={8} fill="none" stroke="var(--line)" />
      <text x={520} y={58} fontSize={11} letterSpacing="0.16em" fill="var(--color-fd-muted-foreground)">
        YOU SAY IT ONCE, EXPLICITLY
      </text>
      {declared.map((t, i) => chip(520, 76 + i * 54, t, `e${i}`))}
      <g className="text-fd-primary">
        <path d="M716,292 L716,312" stroke="currentColor" strokeWidth={2} strokeOpacity={0.7} className="flow-dash" style={{ animationDelay: '0.5s' }} />
      </g>
      <rect x={520} y={314} width={392} height={38} rx={5} fill="var(--color-fd-primary)" fillOpacity={0.1} stroke="var(--color-fd-primary)" strokeOpacity={0.45} />
      <text x={716} y={338} fontSize={12.5} textAnchor="middle" fill="var(--color-fd-foreground)">
        declared — the decorator attaches in the pipeline
      </text>

      {/* the ruled-out third way */}
      <rect x={24} y={400} width={912} height={46} rx={6} fill="none" stroke="#fb7185" strokeOpacity={0.4} strokeDasharray="6 5" />
      <text x={480} y={428} fontSize={12.5} textAnchor="middle" fill="#fb7185" fillOpacity={0.85}>
        ✗ never: runtime assembly scanning · convention guessing · reflection magic
      </text>
    </svg>
  );
}
