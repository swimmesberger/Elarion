import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import type { Metadata } from 'next';
import type { ReactNode } from 'react';
import { AudienceDiagram } from '../_components/ai/audience-diagram';
import { FloorPlan } from '../_components/ai/floor-plan';
import { GateDiagram } from '../_components/ai/gate-diagram';
import { GeneratedRing } from '../_components/ai/generated-ring';
import { RequestJournal } from '../_components/ai/request-journal';
import { RequestPath } from '../_components/ai/request-path';
import { GithubIcon, PAD, Section, SectionTitle, Ticks } from '../_components/section';
import { githubUrl } from '@/lib/shared';

export const metadata: Metadata = {
  title: 'Elarion + AI — why AI-first teams still need a foundation',
  description:
    'The plain-language case for Elarion: every feature you build is instantly usable by AI assistants, your AI reads (and bills) a fraction of the code, mistakes stop at an automatic gate, and every request keeps a journal your AI can be asked about.',
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
          <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">{point.body}</p>
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
          <p className="eyebrow">/// Elarion, explained for decision-makers</p>

          <h1 className="mt-6 font-display text-[2.4rem] font-semibold leading-[1.08] tracking-[-0.03em] text-fd-foreground sm:text-5xl">
            “Why a framework?
            <br />
            The AI writes the code.”
          </h1>

          <p className="mt-6 max-w-xl text-lg leading-relaxed text-fd-muted-foreground">
            Fair question — here is the straight answer. AI didn&apos;t make foundations obsolete;
            it changed what they are for. Less about typing speed. More about keeping machine-speed
            work safe, affordable, and connected to everything — including the AI itself. Four
            arguments. No jargon. Each one checkable.
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
          <dd className="font-display text-4xl font-semibold tracking-[-0.02em] text-fd-primary">
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
        <p className="eyebrow">/// the same guarded path, every time</p>
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
    <Section id="reach" n="01" label="Reach" aside="works with Claude · ChatGPT · Copilot">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
        <SectionTitle
          title="Build a feature once. Every audience gets it — including AI."
          lead={
            <>
              Software used to have two audiences: the people using your app, and the partner
              systems connected to it. There is a third audience now — AI assistants acting on
              your customers&apos; behalf. Elarion treats it as standard equipment: the moment your
              team finishes a feature, it is available to your website, to your partners, and to AI
              assistants through MCP — the open plug standard the AI industry has settled on, the
              way USB became the standard for devices. No second project. No separate
              &ldquo;AI version&rdquo; of your product to build and keep in sync.
            </>
          }
        />
        </div>

        <div className="vt-rise mt-12">
          <div className="overflow-x-auto">
            <div className="min-w-[640px]">
              <AudienceDiagram />
            </div>
          </div>
          <p className="mx-auto mt-6 max-w-2xl text-center text-sm leading-relaxed text-fd-muted-foreground">
            Every path runs through the same checkpoint. An AI assistant obeys exactly the
            permissions a person would — being a machine opens no doors.
          </p>
        </div>

        <div className="vt-rise mt-12 grid gap-8 md:grid-cols-3">
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">No parallel project</h3>
            <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">
              Most companies bolt AI onto their product as a separate initiative, with its own
              budget, its own timeline, and its own bugs. Here it is the same feature, reaching two
              more audiences at no extra cost.
            </p>
          </div>
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">Same locks on every door</h3>
            <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">
              Who may do what is decided in one place, beneath all three audiences. Change a rule
              once and the website, your partners, and every AI assistant obey it — instantly and
              identically.
            </p>
          </div>
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">A standard, not a gamble</h3>
            <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">
              MCP is backed across the industry — Anthropic, OpenAI, Microsoft. You are plugging
              into a standard, not marrying a vendor&apos;s platform.
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
    <Section id="economics" n="02" label="Economics" aside="measured, not estimated">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
        <SectionTitle
          title="AI reads everything, every time. Hand it a shorter book."
          lead={
            <>
              AI assistants are billed by the amount of text they read and write — and before an AI
              can work on your product, it has to read the code around the task. Every unnecessary
              line is a small tax, charged again on every task, forever. Elarion&apos;s answer:
              your people and their AI write only the business decisions. The machinery around
              those decisions — connections, safety checks, record-keeping — is produced
              automatically at every build. What was never written is never read, and never billed.
            </>
          }
        />
        </div>

        <div className="mt-12">
          <GeneratedRing />
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
              <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">{point.body}</p>
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
    <Section id="safety" n="03" label="Safety" aside="60+ automatic checks, every build">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
        <SectionTitle
          title="Mistakes stop at the gate."
          lead={
            <>
              AI writes code fast — and is sometimes confidently wrong. In an Elarion codebase,
              every change, human or AI, must pass a gate of more than sixty automatic checks
              before it can even finish building: security rules that cannot be quietly weakened,
              boundaries between departments&apos; code that cannot be crossed, connections that
              cannot be left half-wired. A change that fails comes back with written instructions —
              which today&apos;s AI reads, applies, and resubmits in seconds. Nothing unchecked
              reaches your customers. Nothing unchecked even reaches your people.
            </>
          }
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
            <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">
              The rule is not &ldquo;remember to lock the door.&rdquo; Doors are locked unless
              someone deliberately opens one — so &ldquo;the AI forgot&rdquo; is not a failure mode
              your company can have.
            </p>
          </div>
          <div className="border-t border-(--line) pt-4">
            <h3 className="font-medium text-fd-foreground">Architecture that survives speed</h3>
            <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">
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
    <Section id="insight" n="04" label="Insight" aside="Microsoft .NET Aspire · OpenTelemetry">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
        <SectionTitle
          title="Every request keeps a journal. Your AI reads it."
          lead={
            <>
              Every customer action in an Elarion system automatically writes a journal entry —
              what happened, in what order, how long each step took — in OpenTelemetry, the
              industry&apos;s standard format, which your monitoring tools already speak. During
              development, Microsoft&apos;s .NET Aspire puts that journal on a live dashboard and
              hands it to AI assistants directly. So when something misbehaves, your AI
              doesn&apos;t guess from the code. It looks at what actually happened — and answers in
              plain language.
            </>
          }
        />
        </div>

        <div className="vt-rise mt-12 grid items-start gap-10 *:min-w-0 lg:grid-cols-[1fr_0.95fr]">
          <RequestJournal />

          <PointList
            points={[
              {
                title: 'From “who knows?” to “here’s why”',
                body: 'Slow afternoons, failed payments, odd spikes — the journal holds the answer, and the AI can be asked in plain English. Diagnosis stops being archaeology.',
              },
              {
                title: 'Zero extra effort',
                body: 'Nobody has to remember to add the record-keeping. The framework writes the journal for every feature, from the first day, including the ones AI built last night.',
              },
              {
                title: 'One story, dev to production',
                body: 'The journal your AI reads while building is the same journal your operations team reads when it matters. Same names, same structure, no blind spots.',
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
    body: 'One set of permissions for humans, partners, and AI — enforced beneath all of them, with no quiet way around it. A change that would weaken the rules does not pass the gate, and every action leaves a journal entry.',
  },
  {
    role: 'For the CTO',
    body: 'Open standards at every boundary, Microsoft tooling underneath, machinery you can read, and an exit that stays open. A foundation your successor will thank you for, not curse you over.',
  },
];

function SeatSection() {
  return (
    <Section id="seats" n="06" label="In your language" aside="one page · three seats">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle title="What it means, seat by seat." />
        </div>
        <div className="vt-rise mt-10 grid gap-5 md:grid-cols-3">
          {seats.map((seat) => (
            <div key={seat.role} className="rounded-[4px] border border-(--line) p-6">
              <p className="eyebrow text-fd-primary">{seat.role}</p>
              <p className="mt-3 text-sm leading-relaxed text-fd-muted-foreground">{seat.body}</p>
            </div>
          ))}
        </div>
      </div>
    </Section>
  );
}

/* -------------------------------------------------------------- 06 · Bet */

const betRows = [
  {
    title: 'Open standards end to end',
    body: 'The connections — to AI, to partners, to monitoring — are industry standards, not proprietary sockets. Any vendor on any side can be swapped without rewriting your product.',
  },
  {
    title: 'Software you own',
    body: 'Elarion is open source under Apache-2.0, the permissive license trusted across the industry. No hosted platform, no per-seat fee, nothing phoning home.',
  },
  {
    title: 'Homework you can check',
    body: 'Twenty-three written decision records explain every major choice, and six hundred automated tests guard them. Your architects can audit the reasoning before you commit a single sprint.',
  },
  {
    title: 'An exit that stays open',
    body: 'If you ever walk away, you keep a conventional, readable .NET codebase your team already understands. Leaving is an afternoon’s decision, not a rewrite.',
  },
];

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

        <div className="vt-rise mt-10 grid gap-8 md:grid-cols-2">
          {betRows.map((row) => (
            <div key={row.title} className="border-t border-(--line) pt-4">
              <h3 className="font-medium text-fd-foreground">{row.title}</h3>
              <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">{row.body}</p>
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
          <p className="mt-4 leading-relaxed text-fd-muted-foreground">
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
