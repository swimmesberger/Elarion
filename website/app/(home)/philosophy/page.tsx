import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import type { Metadata } from 'next';
import { Batteries } from '../_components/philosophy/batteries';
import { FeedbackTimeline } from '../_components/philosophy/feedback-timeline';
import { MaximSplit } from '../_components/philosophy/maxim-split';
import { PipelineDiagram } from '../_components/philosophy/pipeline-diagram';
import { GithubIcon, Mono, PAD, Section, SectionTitle, Ticks } from '../_components/section';
import { githubUrl } from '@/lib/shared';

export const metadata: Metadata = {
  title: 'Philosophy — why Elarion is built this way',
  description:
    'The engineering philosophy behind Elarion, visualized: auto-detect application patterns, explicitly wire platform capabilities. The mental model, the feedback loop, the decorator pipeline, the batteries-and-sockets packaging, and the opinions — each on the record as an ADR.',
};

export default function PhilosophyPage() {
  return (
    <main className="flex flex-1 flex-col overflow-x-clip">
      <div className="mx-auto w-full max-w-[80rem] border-x border-(--line)">
        <Hero />
        <ModelSection />
        <FeedbackSection />
        <PipelineSection />
        <BatteriesSection />
        <OpinionsSection />
        <ClosingCta />
      </div>
    </main>
  );
}

/* ----------------------------------------------------------------- Hero */

function Hero() {
  return (
    <section className="relative border-b border-(--line)">
      <Ticks />
      <div className={`flex flex-col items-center py-16 text-center lg:py-24 ${PAD}`}>
        <p className="eyebrow">/// the philosophy, for engineers</p>

        <h1 className="mt-7 max-w-4xl font-display text-[2.4rem] font-semibold leading-[1.12] tracking-[-0.03em] text-fd-foreground sm:text-5xl">
          “Auto-detect application patterns, explicitly wire platform capabilities.”
        </h1>

        <p className="mt-7 max-w-2xl text-lg leading-relaxed text-fd-muted-foreground">
          One sentence drives every API in the framework. This page unpacks it: the mental model,
          the feedback loop it buys, the pipeline it produces, the batteries it ships — and the
          opinions underneath, every one of them written down and arguable.
        </p>

        <div className="mt-9 flex flex-wrap items-center justify-center gap-3">
          <a href="#model" className="btn-primary">
            Unpack the maxim
            <ArrowRight className="size-4" />
          </a>
          <Link href="/" className="btn-outline">
            See it in code
          </Link>
        </div>
        <p className="mt-5 text-sm text-fd-muted-foreground">
          Prefer long-form prose?{' '}
          <Link href="/docs/why-elarion" className="text-fd-primary hover:underline">
            Why Elarion, in the docs
          </Link>
          .
        </p>
      </div>
    </section>
  );
}

/* ------------------------------------------------------------ 01 · Model */

function ModelSection() {
  return (
    <Section id="model" n="01" label="The mental model" aside="two kinds of truth">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle
            title="Your code already says it — or you say it once."
            lead={
              <>
                A class implementing <Mono>IHandler&lt;,&gt;</Mono> has already told you it handles
                that request; repeating it in a registration list creates a second model that
                drifts. But no amount of code inspection can tell a framework how long to cache a
                result or which retry policy an external call deserves — those are decisions, and
                decisions deserve syntax. Elarion splits the world exactly there: patterns are
                detected and their wiring generated; capabilities are declared once, as attributes,
                where the reviewer can see them. The third option — guessing at runtime — is
                banned outright.
              </>
            }
          />
        </div>

        <div className="vt-rise mt-12 overflow-x-auto">
          <div className="mx-auto min-w-[640px] max-w-5xl">
            <MaximSplit />
          </div>
        </div>
      </div>
    </Section>
  );
}

/* --------------------------------------------------------- 02 · Feedback */

function FeedbackSection() {
  return (
    <Section id="feedback" n="02" label="The feedback loop" aside="keystroke → build → CI">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle
            title="Move every failure to the left."
            lead={
              <>
                The cost of a mistake is a function of when you learn about it. A red squiggle
                costs seconds; a failed build costs minutes; a production incident costs a
                post-mortem. Because Elarion&apos;s wiring is computed at compile time, whole
                classes of failure — the missing registration, the unroutable endpoint, the
                authorization gate that can&apos;t fail closed, the drifted client contract —
                surface where they are cheapest. Determinism isn&apos;t an aesthetic preference;
                it&apos;s a budget decision about where your team pays for its mistakes.
              </>
            }
          />
        </div>

        <div className="vt-rise mt-12 overflow-x-auto">
          <div className="mx-auto min-w-[640px] max-w-5xl">
            <FeedbackTimeline />
          </div>
        </div>
      </div>
    </Section>
  );
}

/* --------------------------------------------------------- 03 · Pipeline */

function PipelineSection() {
  return (
    <Section id="pipeline" n="03" label="The pipeline" aside="one pipeline · every transport">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle
            title="Cross-cutting concerns, cut once."
            lead={
              <>
                Every request — whether it arrives over JSON-RPC, REST, MCP, a scheduled job, or
                an event — runs the same decorator pipeline around your handler. Authorization is
                the outermost functional gate, so a denied caller never warms a cache or opens a
                transaction; domain events dispatch inline inside your transaction while
                integration events wait for the commit. Your handler stays a plain function over
                your domain: it takes a request, returns a{' '}
                <Mono>Result&lt;T&gt;</Mono>, and has no idea HTTP exists.
              </>
            }
          />
        </div>

        <div className="vt-rise mt-12 overflow-x-auto">
          <div className="mx-auto min-w-[720px] max-w-5xl">
            <PipelineDiagram />
          </div>
        </div>
        <p className="vt-rise mx-auto mt-4 max-w-2xl text-center text-sm leading-relaxed text-fd-muted-foreground">
          Each stage attaches only when the handler asks for it — a bare handler compiles to a
          bare call. And because it&apos;s all generated source, you can read the exact chain in
          your build output.
        </p>
      </div>
    </Section>
  );
}

/* -------------------------------------------------------- 04 · Batteries */

function BatteriesSection() {
  return (
    <Section id="batteries" n="04" label="Batteries" aside="ADR-0017 · pay for what you use">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle
            title="Batteries included. Sockets standard."
            lead={
              <>
                Frameworks usually make you choose: batteries included (and a dependency graph you
                didn&apos;t order), or bring-your-own-everything. Elarion&apos;s answer is the
                socket: the seam and its decorator live in the dependency-light core; the battery
                — with its heavy dependency — lives one opt-in package away. A service that never
                caches never ships a cache. And because the seam is public, every battery is
                replaceable without touching a handler.
              </>
            }
          />
        </div>

        <div className="vt-rise mt-12">
          <Batteries />
        </div>
      </div>
    </Section>
  );
}

/* --------------------------------------------------------- 05 · Opinions */

const opinions = [
  {
    stance: 'Events split on the transaction, not on a verb',
    why: 'Domain events commit with the command that raised them; integration events deliver after commit. The phase is the API — not an afterthought.',
    ref: 'ADR-0001',
    href: `${githubUrl}/blob/main/docs/decisions/0001-event-transaction-phase.md`,
  },
  {
    stance: 'The event bus is pub/sub-only',
    why: 'A reply is a typed call with a compile-time contract. Request/reply over a bus is a runtime surprise wearing a messaging costume.',
    ref: 'ADR-0010',
    href: `${githubUrl}/blob/main/docs/decisions/0010-event-bus-is-pub-sub-only.md`,
  },
  {
    stance: 'Start modular, not distributed',
    why: 'Modules are mini bounded contexts with analyzer-enforced walls. Extraction into a service is a graduation along existing contract lines — never a rewrite.',
    ref: 'ADR-0008',
    href: `${githubUrl}/blob/main/docs/decisions/0008-bounded-contexts-and-the-graduation-path.md`,
  },
  {
    stance: 'No repository pattern',
    why: 'Handlers inject the concrete DbContext — no repository, not even a context interface. EF Core is already the abstraction; wrapping it buys ceremony, not portability, and leaks the moment you need raw SQL.',
    ref: 'ADR-0007',
    href: `${githubUrl}/blob/main/docs/decisions/0007-data-is-platform-module-as-plugin.md`,
  },
  {
    stance: 'Idempotency fences on a database row',
    why: 'An INSERT … ON CONFLICT inside your transaction is the lock. No distributed lock service to run, no fencing tokens to get wrong.',
    ref: 'ADR-0021',
    href: `${githubUrl}/blob/main/docs/decisions/0021-idempotency.md`,
  },
  {
    stance: 'A cache may live in Postgres',
    why: 'An UNLOGGED table behind HybridCache beats operating Redis for most applications. Worst case after a crash is a cold cache — never lost data.',
    ref: 'ADR-0020',
    href: `${githubUrl}/blob/main/docs/decisions/0020-postgres-unlogged-l2-cache.md`,
  },
  {
    stance: 'Heavy dependencies are opt-in',
    why: 'The core references Microsoft.Extensions abstractions only. Polly, HybridCache, and OpenFeature arrive when a handler asks for them.',
    ref: 'ADR-0017',
    href: `${githubUrl}/blob/main/docs/decisions/0017-dependency-light-core.md`,
  },
  {
    stance: 'Generated code answers to the framework’s name',
    why: 'Generated infrastructure is framework-named, deterministic, and readable — magic you can diff in code review, not magic you must trust.',
    ref: 'ADR-0018',
    href: `${githubUrl}/blob/main/docs/decisions/0018-generated-infrastructure-is-framework-named.md`,
  },
];

function OpinionsSection() {
  return (
    <Section id="opinions" n="05" label="Opinions" aside="23 ADRs · public reasoning">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="vt-rise">
          <SectionTitle
            title="Opinions, on the record."
            lead={
              <>
                A framework is a set of opinions with a package manager. These are the strongest
                ones — each backed by a written decision record that states the alternatives we
                rejected and why. Disagree with one? Good: argue with the ADR, not with vibes.
              </>
            }
          />
        </div>

        <div className="vt-rise mt-10 grid gap-x-10 gap-y-8 md:grid-cols-2">
          {opinions.map((opinion) => (
            <div key={opinion.stance} className="border-t border-(--line) pt-4">
              <div className="flex items-baseline justify-between gap-4">
                <h3 className="font-medium text-fd-foreground">{opinion.stance}</h3>
                <a
                  href={opinion.href}
                  target={opinion.href.startsWith('http') ? '_blank' : undefined}
                  rel={opinion.href.startsWith('http') ? 'noreferrer' : undefined}
                  className="shrink-0 font-mono text-xs text-fd-primary hover:underline"
                >
                  {opinion.ref} →
                </a>
              </div>
              <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">{opinion.why}</p>
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
            Now see it in code.
          </h2>
          <p className="mt-4 leading-relaxed text-fd-muted-foreground">
            The front page has the meat — real handlers, the generated output, a five-minute
            quickstart. The docs carry the long-form rationale. And if the people who hold the
            budget need convincing,{' '}
            <Link href="/ai" className="text-fd-primary hover:underline">
              there&apos;s a page in their language too
            </Link>
            .
          </p>
          <div className="mt-8 flex flex-wrap items-center gap-3">
            <Link href="/" className="btn-primary">
              The code
              <ArrowRight className="size-4" />
            </Link>
            <Link href="/docs/getting-started/quickstart" className="btn-outline">
              Quickstart
            </Link>
            <a href={`${githubUrl}/tree/main/docs/decisions`} target="_blank" rel="noreferrer" className="btn-outline">
              <GithubIcon className="size-4" />
              Read the ADRs
            </a>
          </div>
        </div>
      </div>
    </section>
  );
}
