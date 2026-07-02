'use client';

import { useState } from 'react';
import { cn } from '@/lib/cn';

/**
 * "Software with a floor plan" — the product drawn as a building: wings with
 * real walls, doorways you chose, one guarded entrance, and a shared
 * infrastructure floor every wing uses. The switch panel is the interactive
 * bit: flipping a wing off dims it everywhere — the plain-language version of
 * module gating and feature flags.
 */

type Wing = {
  id: string;
  name: string;
  x: number;
  features: string[];
};

const WINGS: Wing[] = [
  { id: 'sales', name: 'Sales', x: 70, features: ['quotes', 'orders', 'pipeline'] },
  { id: 'billing', name: 'Billing', x: 270, features: ['invoices', 'payments', 'reminders'] },
  { id: 'reports', name: 'Reports', x: 470, features: ['dashboards', 'exports', 'forecasts'] },
];

const UTILITIES = [
  { x: 86, label: 'security desk', icon: 'lock' as const },
  { x: 228, label: 'records office', icon: 'book' as const },
  { x: 370, label: 'mail room', icon: 'mail' as const },
  { x: 512, label: 'speed & caching', icon: 'bolt' as const },
];

const AUDIENCES = [
  { y: 96, label: 'Your customers', d: 'M740,123 C712,123 716,244 694,252' },
  { y: 226, label: 'Your partners', d: 'M740,253 L694,257' },
  { y: 356, label: 'AI assistants', d: 'M740,383 C712,383 716,268 694,262' },
];

function UtilityIcon({ kind, cx, cy }: { kind: 'lock' | 'book' | 'mail' | 'bolt'; cx: number; cy: number }) {
  const stroke = 'var(--color-fd-primary)';
  switch (kind) {
    case 'lock':
      return (
        <g>
          <path d={`M${cx - 6},${cy - 2} v-4 a6,6 0 0 1 12,0 v4`} fill="none" stroke={stroke} strokeWidth={1.8} />
          <rect x={cx - 8} y={cy - 2} width={16} height={11} rx={2.5} fill="none" stroke={stroke} strokeWidth={1.8} />
        </g>
      );
    case 'book':
      return (
        <g fill="none" stroke={stroke} strokeWidth={1.8}>
          <rect x={cx - 9} y={cy - 8} width={18} height={17} rx={2} />
          <path d={`M${cx - 4},${cy - 3} h8 M${cx - 4},${cy + 1} h8 M${cx - 4},${cy + 5} h5`} />
        </g>
      );
    case 'mail':
      return (
        <g fill="none" stroke={stroke} strokeWidth={1.8}>
          <rect x={cx - 10} y={cy - 7} width={20} height={15} rx={2} />
          <path d={`M${cx - 10},${cy - 6} L${cx},${cy + 2} L${cx + 10},${cy - 6}`} />
        </g>
      );
    case 'bolt':
      return (
        <path
          d={`M${cx + 2},${cy - 9} L${cx - 6},${cy + 1} L${cx - 1},${cy + 1} L${cx - 2},${cy + 9} L${cx + 6},${cy - 1} L${cx + 1},${cy - 1} Z`}
          fill="none"
          stroke={stroke}
          strokeWidth={1.6}
          strokeLinejoin="round"
        />
      );
  }
}

export function FloorPlan() {
  const [on, setOn] = useState<Record<string, boolean>>({ sales: true, billing: true, reports: true });

  return (
    <div className="grid items-center gap-10 *:min-w-0 lg:grid-cols-[1.6fr_1fr]">
      <div>
        <div className="overflow-x-auto">
        <div className="min-w-[640px]">
          <svg
            viewBox="0 0 960 540"
            className="h-auto w-full"
            role="img"
            aria-label="A floor plan of the product: three wings — Sales, Billing, and Reports — each containing features, connected by chosen doorways, above a shared infrastructure floor with a security desk, records office, mail room, and caching. One guarded entrance serves customers, partners, and AI assistants alike."
            xmlns="http://www.w3.org/2000/svg"
          >
            {/* audience wires into the single entrance */}
            {AUDIENCES.map((a) => (
              <g key={a.label} className="text-fd-primary">
                <path d={a.d} fill="none" stroke="var(--line)" strokeWidth={1.5} />
                <path
                  d={a.d}
                  fill="none"
                  stroke="currentColor"
                  strokeWidth={2}
                  strokeOpacity={0.7}
                  strokeLinecap="round"
                  className="flow-dash"
                />
              </g>
            ))}

            {/* the building */}
            <rect x={40} y={40} width={650} height={460} rx={10} fill="var(--color-fd-card)" stroke="var(--color-fd-foreground)" strokeOpacity={0.35} strokeWidth={1.5} />
            <text x={64} y={72} fontSize={12} letterSpacing="0.14em" fill="var(--color-fd-muted-foreground)">
              YOUR PRODUCT — ONE BUILDING
            </text>

            {/* entrance: a gap in the right wall with the checkpoint lock */}
            <rect x={684} y={224} width={12} height={72} fill="var(--color-fd-background)" />
            <g className="text-fd-primary">
              <circle cx={690} cy={260} r={17} fill="var(--color-fd-card)" stroke="currentColor" strokeOpacity={0.6} />
              <circle cx={690} cy={260} r={17} fill="none" stroke="currentColor" strokeOpacity={0.35} strokeWidth={5} className="soft-pulse" />
              <path d="M684,258 v-3.5 a6,6 0 0 1 12,0 v3.5" fill="none" stroke="currentColor" strokeWidth={1.7} />
              <rect x={682} y={258} width={16} height={10.5} rx={2.5} fill="currentColor" />
            </g>
            <text x={690} y={306} fontSize={11} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
              one entrance
            </text>

            {/* wings */}
            {WINGS.map((wing) => {
              const active = on[wing.id];
              return (
                <g key={wing.id} opacity={active ? 1 : 0.18} style={{ transition: 'opacity 0.45s ease' }}>
                  <rect x={wing.x} y={90} width={180} height={210} rx={6} fill="none" stroke="var(--color-fd-foreground)" strokeOpacity={0.3} strokeWidth={1.2} />
                  <text x={wing.x + 16} y={118} fontSize={15} fontWeight={600} fill="var(--color-fd-foreground)">
                    {wing.name}
                  </text>
                  {wing.features.map((feature, i) => (
                    <g key={feature}>
                      <rect
                        x={wing.x + 15}
                        y={134 + i * 46}
                        width={150}
                        height={36}
                        rx={4}
                        fill="var(--color-fd-muted)"
                        stroke="var(--line)"
                      />
                      <text
                        x={wing.x + 90}
                        y={134 + i * 46 + 23}
                        fontSize={12.5}
                        textAnchor="middle"
                        fill="var(--color-fd-muted-foreground)"
                      >
                        {feature}
                      </text>
                    </g>
                  ))}
                  {/* connector down to shared infrastructure */}
                  <line
                    x1={wing.x + 90}
                    y1={300}
                    x2={wing.x + 90}
                    y2={330}
                    stroke="var(--color-fd-primary)"
                    strokeOpacity={0.55}
                    strokeWidth={1.8}
                    strokeDasharray="4 5"
                  />
                </g>
              );
            })}

            {/* CLOSED stamps sit outside the dimmed groups so they stay legible */}
            {WINGS.filter((wing) => !on[wing.id]).map((wing) => (
              <g key={`${wing.id}-closed`}>
                <rect x={wing.x + 44} y={192} width={92} height={26} rx={3} fill="var(--color-fd-card)" stroke="var(--line)" />
                <text x={wing.x + 90} y={209} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)" letterSpacing="0.22em">
                  CLOSED
                </text>
              </g>
            ))}

            {/* doorways between wings */}
            {[250, 450].map((x) => (
              <g key={x}>
                <rect x={x - 1} y={180} width={22} height={10} fill="var(--color-fd-card)" />
                <path d={`M${x},185 h20`} stroke="var(--color-fd-primary)" strokeWidth={2} strokeLinecap="round" strokeOpacity={0.8} />
              </g>
            ))}

            {/* shared infrastructure floor */}
            <rect x={70} y={330} width={580} height={140} rx={8} fill="none" stroke="var(--color-fd-primary)" strokeOpacity={0.45} strokeWidth={1.2} strokeDasharray="6 5" />
            <text x={86} y={356} fontSize={11} letterSpacing="0.14em" fill="var(--color-fd-muted-foreground)">
              SHARED INFRASTRUCTURE — BUILT ONCE, EVERY WING USES IT
            </text>
            {UTILITIES.map((u) => (
              <g key={u.label}>
                <rect x={u.x} y={372} width={122} height={80} rx={5} fill="var(--color-fd-muted)" stroke="var(--line)" />
                <UtilityIcon kind={u.icon} cx={u.x + 61} cy={402} />
                <text x={u.x + 61} y={438} fontSize={11.5} textAnchor="middle" fill="var(--color-fd-muted-foreground)">
                  {u.label}
                </text>
              </g>
            ))}

            {/* audiences */}
            {AUDIENCES.map((a) => (
              <g key={a.label}>
                <rect x={740} y={a.y} width={186} height={54} rx={8} fill="var(--color-fd-card)" stroke="var(--line)" />
                <text x={833} y={a.y + 33} fontSize={13.5} fontWeight={600} textAnchor="middle" fill="var(--color-fd-foreground)">
                  {a.label}
                </text>
              </g>
            ))}
          </svg>
        </div>
        </div>
        <p className="mt-4 text-center text-sm leading-relaxed text-fd-muted-foreground">
          Wings connect only through doorways you chose — and everyone, including AI, comes in
          through the one guarded entrance.
        </p>
      </div>

      {/* the master switches */}
      <div>
        <p className="eyebrow">Try it — the master switches</p>
        <div className="mt-4 space-y-3">
          {WINGS.map((wing) => {
            const active = on[wing.id];
            return (
              <button
                key={wing.id}
                type="button"
                role="switch"
                aria-checked={active}
                onClick={() => setOn((prev) => ({ ...prev, [wing.id]: !prev[wing.id] }))}
                className={cn(
                  'flex w-full items-center justify-between gap-4 rounded-[4px] border px-4 py-3 text-left transition-colors',
                  active ? 'border-(--line) bg-fd-card' : 'border-(--line-soft) bg-transparent',
                )}
              >
                <span>
                  <span className={cn('block text-sm font-medium', active ? 'text-fd-foreground' : 'text-fd-muted-foreground')}>
                    The {wing.name} wing
                  </span>
                  <span className="block text-xs text-fd-muted-foreground">
                    {active ? 'open — visible to every audience' : 'closed — gone from every audience'}
                  </span>
                </span>
                <span
                  aria-hidden
                  className={cn(
                    'relative h-[22px] w-10 shrink-0 rounded-full border transition-colors',
                    active ? 'border-fd-primary bg-fd-primary' : 'border-(--line) bg-fd-muted',
                  )}
                >
                  <span
                    className={cn(
                      'absolute top-[2px] size-4 rounded-full bg-white shadow-sm transition-[left]',
                      active ? 'left-[19px]' : 'left-[2px]',
                    )}
                  />
                </span>
              </button>
            );
          })}
        </div>
        <p className="mt-4 text-sm leading-relaxed text-(--body)">
          Not a mock-up: a wing really can be switched off in configuration, and its features
          vanish from the website, from partners, and from AI assistants at once — no half-removed
          leftovers. Individual features can also be dimmed live, per customer, for careful
          rollouts.
        </p>
      </div>
    </div>
  );
}
