import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import type { Metadata } from 'next';
import type { ReactNode } from 'react';
import { AudienceDiagram } from '../_components/ai/audience-diagram';
import { BetPanel } from '../_components/ai/bet-panel';
import { FloorPlan } from '../_components/ai/floor-plan';
import { GateDiagram } from '../_components/ai/gate-diagram';
import { GeneratedRing } from '../_components/ai/generated-ring';
import { RequestJournal } from '../_components/ai/request-journal';
import { RequestPath } from '../_components/ai/request-path';
import { GithubIcon, PAD, Section, SectionTitle, Ticks } from '../_components/section';
import { githubUrl } from '@/lib/shared';
import { adrCount } from '@/lib/adr-catalog';

export const metadata: Metadata = {
  title: 'Elarion + AI — why AI-first teams still need a foundation',
  description:
    'The plain-language case for Elarion: expose selected application operations to AI through the same contracts and policy pipeline, generate repetitive wiring, catch mistakes at build time, and export structured operational evidence.',
  alternates: { canonical: '/ai' },
};

export default function AiPage() {
  return (
    <main className="flex flex-1 flex-col overflow-x-clip">
      <div className="mx-auto w-full max-w-[80rem] border-x border-(--line)">
        <Hero />
        <PathBand />
        <ReachSection />
        <EconomicsSection />
        <GateSection />
        <JournalSection />
        <OrderSection />
        <SeatSection />
        <BetSection />
        <ClosingCta />
      </div>
    </main>
  );
}

/* ---------------------------------------------------------------- shared */

function PointList({ points }: { points: { title: string; body: ReactNode }[] }) {
  return (
    <div className="space-y-6">
      {points.map((point) => (
        <div key={point.title} className="border-t border-(--line) pt-4">
          <h3 className="font-medium text-fd-foreground">{point.title}</h3>
          <p className="mt-2 text-sm leading-relaxed text-(--body)">{point.body}</p>
        </div>
      ))}
    </div>
  );
}

/* ----------------------------------------------------------------- Hero */

function Hero() {
  return (
    <section className="relative border-b border-(--line)">
      <Ticks />
      <div className={`grid grid-cols-1 items-center gap-12 py-16 *:min-w-0 lg:grid-cols-[1.05fr_0.95fr] lg:gap-14 lg:py-24 ${PAD}`}>
        <div className="min-w-0">
          <p className="eyebrow">
            <span className="text-(--accent-brand)">///</span> Elarion, explained for decision-makers
          </p>

          <h1 className="mt-6 font-display text-[2.4rem] font-semibold leading-[1.08] tracking-[-0.03em] text-fd-foreground sm:text-5xl">
            “Why a framework?
            <br />
            The AI writes the code.”
          </h1>

          <p className="mt-6 max-w-xl text-lg leading-relaxed text-(--body)">
            Fair question — here is the straight answer. AI didn&apos;t make foundations obsolete;
            it changed what they are for. Less about typing speed. More about keeping machine-speed
            work{' '}
            <span className="font-medium text-fd-foreground">safe, affordable, and connected</span>{' '}
            to everything — including the AI itself. Four arguments. No jargon. Each one checkable.
          </p>

          <div className="mt-9 flex flex-wrap items-center gap-3">
            <a href="#reach" className="btn-primary">
              The four arguments
              <ArrowRight className="size-4" />
            </a>
            <Link href="/" className="btn-outline">
              The technical version
            </Link>
          </div>
        </div>

        <StatPanel />
      </div>
    </section>
  );
}

function StatPanel() {
  return (
    <div className="min-w-0 rounded-[4px] border border-(--line)">
      <div className="border-b border-(--line-soft) px-5 py-2.5">
        <span className="eyebrow">One number to hold on to</span>
      </div>
      <dl>
        <div className="flex items-baseline justify-between gap-6 border-b border-(--line-soft) px-5 py-5">
          <dt className="max-w-[15rem] text-sm leading-snug text-fd-muted-foreground">
            Lines your people — and their AI — actually write
          </dt>
          <dd className="font-display text-4xl font-semibold tracking-[-0.02em] text-fd-foreground">
            ≈1,000
          </dd>
        </div>
        <div className="flex items-baseline justify-between gap-6 border-b border-(--line-soft) px-5 py-5">
          <dt className="max-w-[15rem] text-sm leading-snug text-fd-muted-foreground">
            Lines the machinery adds by itself, on every build
          </dt>
          <dd className="font-display text-4xl font-semibold tracking-[-0.02em] text-(--accent-gen)">
            ≈3,500
          </dd>
        </div>
      </dl>
      <p className="px-5 py-3 text-xs leading-relaxed text-fd-muted-foreground">
        Measured on the sample application that ships with Elarion. Your engineers can reproduce
        the count with a single command.
      </p>
    </div>
  );
}

/* -------------------------------------------------------- The path band */

function PathBand() {
  return (
    <section className="border-b border-(--line)">
      <div className={`py-10 ${PAD}`}>
        <p className="eyebrow">
          <span className="text-(--accent-brand)">///</span> the same guarded path, every time
        </p>
        <div className="mt-6 overflow-x-auto">
          <div className="min-w-[640px]">
            <RequestPath />
          </div>
        </div>
      </div>
    </section>
  );
}

/* ------------------------------------------------------------ 01 · Reach */

function ReachSection() {
  return (
    <Section id="reach" n="01" label="Reach" aside="MCP · one contract · one policy pipeline">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
        <SectionTitle
          title="Build a feature once. Expose the same contract to every audience."
          lead={
            <>
              Software has a third audience now — AI assistants acting on your customers&apos;
              behalf. A handler you select for MCP can serve that audience through the same
              operation contract and policy pipeline as your HTTP and JSON-RPC clients.
            </>
          }
          points={[
            <>
              Through <span className="text-fd-foreground">MCP</span> — an open protocol for
              exposing tools and context to compatible AI clients.
            </>,
            <>No second implementation or separate &ldquo;AI version&rdquo; of an operation to keep in sync.</>,
            <>One set of permissions for people, partners, and AI alike.</>,
          ]}
        />
        </div>

        <div className="vt-rise mt-12">
          <div className="overflow-x-auto">
            <div className="min-w-[640px]">
              <AudienceDiagram />
            </div>
          </div>
          <p className="mx-auto mt-6 max-w-2xl text-center text-sm leading-relaxed text-(--body)">
            Every path runs through the same checkpoint. An AI assistant obeys exactly the
            permissions a person would — being a machine opens no doors.
          </p>
        </div>

        <div className="vt-rise mt-12 grid gap-8 md:grid-cols-3">
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">No parallel project</h3>
            <p className="mt-2 text-sm leading-relaxed text-(--body)">
              Most companies bolt AI onto their product as a separate initiative, with its own
              budget, its own timeline, and its own bugs. Here it is the same feature, reaching two
              more audiences at no extra cost.
            </p>
          </div>
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">Same locks on every door</h3>
            <p className="mt-2 text-sm leading-relaxed text-(--body)">
              Who may do what is decided in one place, beneath all three audiences. Change a rule
              once and the website, your partners, and every AI assistant obey it — instantly and
              identically.
            </p>
          </div>
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">A shared protocol, not a custom bridge</h3>
            <p className="mt-2 text-sm leading-relaxed text-(--body)">
              MCP keeps the AI-facing boundary separate from any one client. Compatible clients
              consume the same operation descriptions and schemas instead of a vendor-specific tool layer.
            </p>
          </div>
        </div>
      </div>
    </Section>
  );
}

/* -------------------------------------------------------- 02 · Economics */

function EconomicsSection() {
  return (
    <Section id="economics" n="02" label="Economics" aside="measured, not estimated" tinted>
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
        <SectionTitle
          title="AI reads everything, every time. Hand it a shorter book."
          lead={
            <>
              AI assistants are billed by the text they read and write — and before an AI can work
              on your product, it has to read the code around the task.
            </>
          }
          points={[
            <>Every unnecessary line is a small tax, charged again on every task, forever.</>,
            <>Your people and their AI write only the business decisions; the machinery is produced at every build.</>,
            <>
              <span className="text-fd-foreground">What was never written is never read — and never billed.</span>
            </>,
          ]}
        />
        </div>

        <div className="mt-12">
          <GeneratedRing />
        </div>

        <div className="vt-rise mx-auto mt-12 max-w-3xl rounded-[4px] border border-(--line) p-6">
          <p className="eyebrow text-fd-primary">Field report — a real migration, not the sample</p>
          <p className="mt-3 text-sm leading-relaxed text-fd-muted-foreground">
            The production application Elarion was extracted from replaced its home-grown
            foundation with the released packages in a single pull request:{' '}
            <span className="font-medium text-fd-foreground">
              391 lines added, 16,223 removed
            </span>
            . Sixteen thousand lines that team no longer maintains, reviews — or pays an AI to
            read — ever again.
          </p>
        </div>

        <div className="vt-rise mt-12 grid gap-8 md:grid-cols-3">
          {[
            {
              title: 'The bill follows the reading',
              body: 'When 78% of the finished product writes itself, your AI works from less than a quarter of the text it would otherwise wade through — on every task, every day, across every team.',
            },
            {
              title: 'What nobody writes, nobody gets wrong',
              body: 'Machine-produced lines never need a human review. And your senior engineers’ review hours are the scarcest — and most expensive — resource you have.',
            },
            {
              title: 'It compounds',
              body: 'This is not a one-time saving. Every future feature, every future fix, every future AI task starts from the shorter book. It is the difference between paying interest and earning it.',
            },
          ].map((point) => (
            <div key={point.title} className="border-t border-(--line) pt-4">
              <h3 className="font-medium text-fd-foreground">{point.title}</h3>
              <p className="mt-2 text-sm leading-relaxed text-(--body)">{point.body}</p>
            </div>
          ))}
        </div>
      </div>
    </Section>
  );
}

/* ----------------------------------------------------------- 03 · Safety */

function GateSection() {
  return (
    <Section id="safety" n="03" label="Safety" aside="100+ automatic checks, every build">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
        <SectionTitle
          title="Mistakes stop at the gate."
          lead={
            <>
              AI writes code fast — and is sometimes confidently wrong. So every change, human or
              AI, passes a gate of more than one hundred automatic checks before it can even finish
              building.
            </>
          }
          points={[
            <>Security rules that cannot be quietly weakened; boundaries that cannot be crossed.</>,
            <>A failed change comes back with written instructions — the AI applies them and resubmits in seconds.</>,
            <>
              <span className="text-fd-foreground">Nothing unchecked reaches your people — let alone your customers.</span>
            </>,
          ]}
        />
        </div>

        <div className="vt-rise mt-12 overflow-x-auto">
          <div className="mx-auto min-w-[640px] max-w-4xl">
            <GateDiagram />
          </div>
        </div>

        <div className="vt-rise mt-10 grid gap-8 md:grid-cols-2">
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">Security that cannot rot</h3>
            <p className="mt-2 text-sm leading-relaxed text-(--body)">
              The rule is not &ldquo;remember to lock the door.&rdquo; Doors are locked unless
              someone deliberately opens one — so &ldquo;the AI forgot&rdquo; is not a failure mode
              your company can have.
            </p>
          </div>
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">Architecture that survives speed</h3>
            <p className="mt-2 text-sm leading-relaxed text-(--body)">
              The structure your architects designed is enforced by the gate itself. It holds on
              the hundredth AI-written change, at two in the morning, with nobody watching.
            </p>
          </div>
        </div>
      </div>
    </Section>
  );
}

/* ---------------------------------------------------------- 04 · Insight */

function JournalSection() {
  return (
    <Section id="insight" n="04" label="Insight" aside="Microsoft .NET Aspire · OpenTelemetry" tinted>
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
        <SectionTitle
          title="Every request leaves a trace. Important actions can keep an audit record."
          lead={
            <>
              Elarion instruments every handler invocation with structured spans and metrics. Once
              the host configures an exporter, operations tools can show what ran and how long it
              took. Mark business-critical handlers <code>[Auditable]</code> when they also need a
              durable action record.
            </>
          }
          points={[
            <>
              In <span className="text-fd-foreground">OpenTelemetry</span>, the industry&apos;s
              standard format — your monitoring tools already speak it.
            </>,
            <>Microsoft&apos;s .NET Aspire can visualize local OpenTelemetry traces; production exporters send the same evidence to your chosen backend.</>,
            <>AI integrations can analyze exported telemetry and audit data — the connection is host-owned, explicit, and replaceable.</>,
          ]}
        />
        </div>

        <div className="vt-rise mt-12 grid items-start gap-10 *:min-w-0 lg:grid-cols-[1fr_0.95fr]">
          <RequestJournal />

          <PointList
            points={[
              {
                title: 'From “who knows?” to “here’s why”',
                body: 'Slow afternoons, failed payments, odd spikes — exported traces provide timing and correlation evidence, while audited actions provide durable outcomes. Diagnosis starts from facts instead of code-only guesses.',
              },
              {
                title: 'Instrumentation without handler boilerplate',
                body: 'The handler span and framework metrics are emitted centrally, so feature code does not hand-roll timing. Exporters and retention remain deliberate host and operations choices.',
              },
              {
                title: 'Audit is an explicit policy',
                body: 'Apply [Auditable] only where a durable business record is warranted. The audit entry carries outcome, resource context, and the trace id so it can be correlated with telemetry.',
              },
            ]}
          />
        </div>
      </div>
    </Section>
  );
}

/* ------------------------------------------------------------ 05 · Order */

function OrderSection() {
  return (
    <Section id="order" n="05" label="Order" aside="a plan that stays true">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle
            title="Software with a floor plan."
            lead={
              <>
                Most software grows like an unplanned city — and AI-speed building makes the sprawl
                arrive years earlier. Elarion gives your product a floor plan: wings with real
                walls, doorways you chose, one guarded entrance, and a shared infrastructure floor
                every wing uses instead of rebuilding its own. The gate from argument 03 enforces
                the plan on every change — so the drawing still matches the building years from
                now.
              </>
            }
          />
        </div>

        <div className="vt-rise mt-12">
          <FloorPlan />
        </div>
      </div>
    </Section>
  );
}

/* ------------------------------------------------------------- 06 · Seats */

const seats = [
  {
    role: 'For the CFO',
    body: 'AI spend that scales with ambition, not plumbing. You pay people — and their AI — to write business decisions; the repetitive four-fifths produces itself. Fewer written lines also means fewer expensive review hours.',
  },
  {
    role: 'For the CISO',
    body: 'One set of permissions for humans, partners, and AI — enforced beneath all of them, with no quiet way around it. A change that would weaken the rules does not pass the gate, and auditable actions can keep a durable outcome record.',
  },
  {
    role: 'For the CTO',
    body: 'Open standards at every boundary, Microsoft tooling underneath, machinery you can read, and an exit that stays open. A foundation your successor will thank you for, not curse you over.',
  },
];

function SeatSection() {
  return (
    <Section id="seats" n="06" label="In your language" aside="one page · three seats" tinted>
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle title="What it means, seat by seat." />
        </div>
        <div className="vt-rise mt-10 grid gap-5 md:grid-cols-3">
          {seats.map((seat) => (
            <div key={seat.role} className="rounded-[4px] border border-(--line) p-6">
              <p className="eyebrow text-(--accent-brand)">{seat.role}</p>
              <p className="mt-3 text-sm leading-relaxed text-(--body)">{seat.body}</p>
            </div>
          ))}
        </div>
      </div>
    </Section>
  );
}

/* -------------------------------------------------------------- 06 · Bet */

type BetIconKind = 'plug' | 'key' | 'magnifier' | 'door';

const betRows: { title: string; body: string; icon: BetIconKind }[] = [
  {
    title: 'Open standards end to end',
    body: 'The connections — to AI, to partners, to monitoring — are industry standards, not proprietary sockets. Any vendor on any side can be swapped without rewriting your product.',
    icon: 'plug',
  },
  {
    title: 'Software you own',
    body: 'Elarion is open source under Apache-2.0, the permissive license trusted across the industry. No hosted platform, no per-seat fee, nothing phoning home.',
    icon: 'key',
  },
  {
    title: 'Homework you can check',
    body: `${adrCount} written decision records explain the major choices, and more than fifteen hundred automated tests guard them. Your architects can audit the reasoning before you commit a single sprint.`,
    icon: 'magnifier',
  },
  {
    title: 'An exit that stays open',
    body: 'If you ever walk away, you keep a conventional, readable .NET codebase your team already understands. Leaving is an afternoon’s decision, not a rewrite.',
    icon: 'door',
  },
];

/** Stroke icons for the bet rows — iris to match the panel's seals. */
function BetIcon({ kind }: { kind: BetIconKind }) {
  const common = {
    fill: 'none' as const,
    stroke: 'var(--accent-brand)',
    strokeWidth: 1.7,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
  };
  return (
    <svg viewBox="0 0 24 24" className="size-[22px] shrink-0" aria-hidden xmlns="http://www.w3.org/2000/svg">
      {kind === 'plug' && (
        <>
          <path d="M9 3.5 v3.5 M15 3.5 v3.5" {...common} />
          <rect x={6.5} y={7} width={11} height={6.5} rx={2} {...common} />
          <path d="M12 13.5 v2.5 c0 2.2 -1.8 3.5 -4 3.5" {...common} />
        </>
      )}
      {kind === 'key' && (
        <>
          <circle cx={7.5} cy={7.5} r={3.3} {...common} />
          <path d="M10 10 L20 20 M15.5 15.5 l2.2 -2.2 M18 18 l2.2 -2.2" {...common} />
        </>
      )}
      {kind === 'magnifier' && (
        <>
          <circle cx={10.5} cy={10.5} r={5.6} {...common} />
          <path d="M14.6 14.6 L20.5 20.5 M8.2 10.7 l1.7 1.7 l3.2 -3.6" {...common} />
        </>
      )}
      {kind === 'door' && (
        <>
          <path d="M13.5 3.5 H6 a1.5 1.5 0 0 0 -1.5 1.5 v14 a1.5 1.5 0 0 0 1.5 1.5 h7.5" {...common} />
          <path d="M12.5 12 h8 M17 8.8 L20.5 12 L17 15.2" {...common} />
        </>
      )}
    </svg>
  );
}

function BetSection() {
  return (
    <Section id="bet" n="07" label="The bet" aside="open source · no lock-in">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle
            title="The bet you're actually making."
            lead="Adopting any foundation is a commitment — so this one is engineered to be a small
              one, and a reversible one."
          />
        </div>

        <div className="vt-rise mt-12 overflow-x-auto">
          <div className="mx-auto min-w-[720px] max-w-4xl">
            <BetPanel />
          </div>
        </div>

        <div className="vt-rise mt-10 grid gap-8 md:grid-cols-2">
          {betRows.map((row) => (
            <div key={row.title} className="border-t border-(--line) pt-4">
              <div className="flex items-center gap-3">
                <BetIcon kind={row.icon} />
                <h3 className="font-medium text-fd-foreground">{row.title}</h3>
              </div>
              <p className="mt-2.5 text-sm leading-relaxed text-(--body)">{row.body}</p>
            </div>
          ))}
        </div>
      </div>
    </Section>
  );
}

/* -------------------------------------------------------------- Closing */

function ClosingCta() {
  return (
    <section className="relative border-b border-(--line)">
      <Ticks />
      <div className={`py-16 lg:py-20 ${PAD}`}>
        <div className="max-w-3xl">
          <h2 className="font-display text-3xl font-semibold tracking-[-0.02em] text-fd-foreground sm:text-4xl">
            Forward this to your tech lead.
          </h2>
          <p className="mt-4 leading-relaxed text-(--body)">
            They&apos;ll want the version with the code — it&apos;s one page away. And because
            Elarion&apos;s documentation is also published in a format AI assistants read natively,
            your own AI can evaluate it exactly the way your engineers will.
          </p>
          <div className="mt-8 flex flex-wrap items-center gap-3">
            <Link href="/" className="btn-primary">
              The technical version
              <ArrowRight className="size-4" />
            </Link>
            <Link href="/docs" className="btn-outline">
              The documentation
            </Link>
            <a href={githubUrl} target="_blank" rel="noreferrer" className="btn-outline">
              <GithubIcon className="size-4" />
              GitHub
            </a>
          </div>
        </div>
      </div>
    </section>
  );
}
