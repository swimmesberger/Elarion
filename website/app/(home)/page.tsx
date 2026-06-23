import Link from 'next/link';
import {
  Activity,
  ArrowRight,
  Binary,
  Bot,
  Boxes,
  CalendarClock,
  CheckCircle2,
  Database,
  GitBranch,
  Plug,
  Shuffle,
  Sparkles,
  Star,
  Terminal,
  XCircle,
  type LucideIcon,
} from 'lucide-react';
import { CodeWindow } from './_components/code-window';
import { CopyCommand } from './_components/copy-button';
import { FanoutDiagram } from './_components/fanout-diagram';
import { Reveal } from './_components/reveal';
import { LogoMark } from '@/components/logo';
import { appTagline, githubUrl } from '@/lib/shared';

export default function HomePage() {
  return (
    <main className="flex flex-1 flex-col overflow-hidden">
      <Hero />
      <TrustStrip />
      <Fanout />
      <Features />
      <Comparison />
      <Philosophy />
      <Architecture />
      <FinalCta />
      <SiteFooter />
    </main>
  );
}

/* ----------------------------------------------------------------- Hero */

function Hero() {
  return (
    <section className="relative isolate border-b border-fd-border">
      {/* atmosphere */}
      <div aria-hidden className="pointer-events-none absolute inset-0 -z-10 bg-grid opacity-[0.45]" />
      <div
        aria-hidden
        className="pointer-events-none absolute inset-x-0 top-0 -z-10 h-px bg-gradient-to-r from-transparent via-azure/60 to-transparent"
      />
      <div
        aria-hidden
        className="drift pointer-events-none absolute -top-32 -left-24 -z-10 size-[34rem] rounded-full bg-iris/20 blur-[120px]"
      />
      <div
        aria-hidden
        className="drift pointer-events-none absolute -top-20 right-[-10rem] -z-10 size-[30rem] rounded-full bg-aqua/15 blur-[120px]"
        style={{ animationDelay: '2s' }}
      />

      <div className="mx-auto grid w-full max-w-7xl items-center gap-14 px-6 py-20 lg:grid-cols-[1.05fr_1fr] lg:py-28">
        <div className="flex flex-col items-start">
          <span className="rise eyebrow flex items-center gap-2" style={{ animationDelay: '0ms' }}>
            <span className="size-1.5 rounded-full bg-aqua" />
            Application framework for .NET
          </span>

          <h1
            className="rise mt-6 font-display text-5xl font-semibold leading-[1.02] tracking-[-0.03em] text-fd-foreground sm:text-6xl lg:text-[4.4rem]"
            style={{ animationDelay: '80ms' }}
          >
            Declare intent.
            <br />
            <span className="text-gradient">Generate the wiring.</span>
          </h1>

          <p
            className="rise mt-6 max-w-xl text-lg leading-relaxed text-fd-muted-foreground"
            style={{ animationDelay: '160ms' }}
          >
            Elarion turns modules, handlers, and attributes into deterministic, compile-time
            wiring. One annotated class becomes a use case, a service, a JSON-RPC method, an MCP
            tool, an HTTP endpoint, and a typed client — with{' '}
            <span className="font-medium text-fd-foreground">no runtime reflection scanning</span>.
          </p>

          <div
            className="rise mt-9 flex flex-wrap items-center gap-3"
            style={{ animationDelay: '240ms' }}
          >
            <Link href="/docs/getting-started/installation" className="btn-brand">
              Get started
              <ArrowRight className="size-4" />
            </Link>
            <Link href="/docs/philosophy" className="btn-ghost">
              Read the philosophy
            </Link>
            <a
              href={githubUrl}
              target="_blank"
              rel="noreferrer"
              className="btn-ghost"
            >
              <Star className="size-4" />
              Star on GitHub
            </a>
          </div>

          <div className="rise mt-8 w-full max-w-md" style={{ animationDelay: '320ms' }}>
            <CopyCommand command="dotnet add package Elarion" />
          </div>
        </div>

        <div className="rise lg:justify-self-end" style={{ animationDelay: '260ms' }}>
          <CodeWindow filename="Clients/GetClient.cs" badge="C# 14" className="w-full max-w-xl">
            <HandlerSnippet />
          </CodeWindow>
        </div>
      </div>
    </section>
  );
}

function HandlerSnippet() {
  return (
    <pre className="whitespace-pre">
      <span className="a">[RpcMethod(</span>
      <span className="s">&quot;clients.get&quot;</span>
      <span className="a">)]</span>
      {'\n'}
      <span className="k">public sealed class</span> <span className="t">GetClient</span>
      <span className="p">(</span>
      <span className="t">IAppDbContext</span> db<span className="p">)</span>
      {'\n  '}
      <span className="p">:</span> <span className="t">IHandler</span>
      <span className="p">&lt;</span>GetClient.<span className="t">Query</span>
      <span className="p">,</span> <span className="t">Result</span>
      <span className="p">&lt;</span>
      <span className="t">Response</span>
      <span className="p">&gt;&gt; {'{'}</span>
      {'\n'}
      {'\n  '}
      <span className="k">public sealed record</span> <span className="t">Query</span>
      <span className="p">(</span>
      <span className="t">Guid</span> Id<span className="p">);</span>
      {'\n  '}
      <span className="k">public sealed record</span> <span className="t">Response</span>
      <span className="p">(</span>
      <span className="t">Guid</span> Id<span className="p">,</span> <span className="t">string</span> Name
      <span className="p">);</span>
      {'\n'}
      {'\n  '}
      <span className="k">public async</span> <span className="t">ValueTask</span>
      <span className="p">&lt;</span>
      <span className="t">Result</span>
      <span className="p">&lt;</span>
      <span className="t">Response</span>
      <span className="p">&gt;&gt;</span> <span className="f">HandleAsync</span>
      <span className="p">(</span>Query q<span className="p">,</span> <span className="t">CancellationToken</span> ct
      <span className="p">) {'{'}</span>
      {'\n    '}
      <span className="k">var</span> client <span className="p">=</span> <span className="k">await</span> db.Clients
      {'\n      '}
      <span className="p">.</span>
      <span className="f">Where</span>
      <span className="p">(</span>c <span className="p">=&gt;</span> c.Id <span className="p">==</span> q.Id
      <span className="p">)</span>
      {'\n      '}
      <span className="p">.</span>
      <span className="f">Select</span>
      <span className="p">(</span>c <span className="p">=&gt;</span> <span className="k">new</span> <span className="t">Response</span>
      <span className="p">(</span>c.Id<span className="p">,</span> c.Name<span className="p">))</span>
      {'\n      '}
      <span className="p">.</span>
      <span className="f">FirstOrDefaultAsync</span>
      <span className="p">(</span>ct<span className="p">);</span>
      {'\n'}
      {'\n    '}
      <span className="k">return</span> client <span className="k">is null</span>
      {'\n      '}
      <span className="p">?</span> <span className="t">AppError</span>
      <span className="p">.</span>
      <span className="f">NotFound</span>
      <span className="p">(</span>
      <span className="s">$&quot;Client {'{'}q.Id{'}'} not found.&quot;</span>
      <span className="p">)</span>
      {'\n      '}
      <span className="p">:</span> client<span className="p">;</span>
      {'\n  '}
      <span className="p">{'}'}</span>
      {'\n'}
      <span className="p">{'}'}</span>
    </pre>
  );
}

/* ---------------------------------------------------------- Trust strip */

const trustItems = [
  '.NET 9 · C# 14',
  'AOT & trim-safe',
  'Zero runtime reflection',
  'OpenTelemetry built in',
  'Inspectable generated code',
];

function TrustStrip() {
  return (
    <section className="border-b border-fd-border bg-fd-muted/30">
      <div className="mx-auto flex w-full max-w-7xl flex-wrap items-center justify-center gap-x-8 gap-y-3 px-6 py-6">
        {trustItems.map((item) => (
          <span key={item} className="font-mono text-xs tracking-wide text-fd-muted-foreground">
            {item}
          </span>
        ))}
      </div>
    </section>
  );
}

/* --------------------------------------------------------------- Fanout */

function Fanout() {
  return (
    <section className="relative border-b border-fd-border py-24">
      <div className="mx-auto grid w-full max-w-7xl items-center gap-14 px-6 lg:grid-cols-[0.85fr_1.15fr]">
        <Reveal className="flex flex-col items-start">
          <SectionEyebrow>The core idea</SectionEyebrow>
          <h2 className="mt-4 max-w-md font-display text-4xl font-semibold tracking-[-0.02em] text-fd-foreground">
            One class. <span className="text-gradient">Every surface.</span>
          </h2>
          <p className="mt-5 max-w-md text-fd-muted-foreground">
            Your application assemblies declare modules and handlers. Source generators emit the
            registrations, transports, and contracts as ordinary DI code at build time. Nothing is
            discovered at startup, so missing wiring is a build error — never a runtime surprise.
          </p>
          <ul className="mt-7 space-y-3">
            {[
              'No registration lists to keep in sync',
              'Deterministic, AOT-friendly startup',
              'The same handler, surfaced to humans and AI agents',
            ].map((point) => (
              <li key={point} className="flex items-start gap-3 text-sm text-fd-foreground">
                <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-aqua" />
                {point}
              </li>
            ))}
          </ul>
          <Link
            href="/docs/source-generation"
            className="group mt-8 inline-flex items-center gap-2 font-medium text-fd-primary"
          >
            How source generation works
            <ArrowRight className="size-4 transition-transform group-hover:translate-x-1" />
          </Link>
        </Reveal>

        <Reveal delay={120} className="rounded-3xl border border-fd-border bg-fd-card/40 p-5 sm:p-8">
          <FanoutDiagram />
        </Reveal>
      </div>
    </section>
  );
}

/* ------------------------------------------------------------- Features */

type Feature = {
  icon: LucideIcon;
  title: string;
  body: string;
  href: string;
  span?: string;
};

const features: Feature[] = [
  {
    icon: Binary,
    title: 'Compile-time, not reflection',
    body: 'Handlers, services, validators, modules, RPC maps, and scheduled jobs are generated as ordinary DI code. Startup is deterministic and AOT-friendly; missing wiring is a build error.',
    href: '/docs/source-generation',
    span: 'lg:col-span-3',
  },
  {
    icon: Boxes,
    title: 'Modules own their surface',
    body: 'A module is a namespace plus an [AppModule] marker. Add a handler under it and the module publishes it automatically through generated, feature-gated registrations.',
    href: '/docs/concepts/modules',
    span: 'lg:col-span-3',
  },
  {
    icon: Plug,
    title: 'End-to-end JSON-RPC',
    body: 'Mark a handler, export a schema at build time, and generate a typed TypeScript + Zod client — without hand-writing DTOs.',
    href: '/docs/json-rpc',
    span: 'lg:col-span-2',
  },
  {
    icon: Bot,
    title: 'AI-native MCP tools',
    body: 'Expose the same handlers to AI agents as an MCP server. Tool names, descriptions, and schemas are generated from your code.',
    href: '/docs/json-rpc/mcp',
    span: 'lg:col-span-2',
  },
  {
    icon: Shuffle,
    title: 'Transport-neutral results',
    body: 'Handlers return Result<T> with a transport-agnostic AppError. The host maps failures to JSON-RPC, HTTP, or any protocol.',
    href: '/docs/concepts/results-and-errors',
    span: 'lg:col-span-2',
  },
  {
    icon: CalendarClock,
    title: 'In-process scheduling',
    body: 'Source-generated jobs share one scheduler with explicit overlap, misfire, and resilience policies — fully instrumented.',
    href: '/docs/scheduling',
    span: 'lg:col-span-2',
  },
  {
    icon: Database,
    title: 'EF Core & blob storage',
    body: 'Generate DbSets and entity configuration, page with keyset cursors, and depend on provider-neutral, streaming-first blob contracts.',
    href: '/docs/entity-framework',
    span: 'lg:col-span-2',
  },
  {
    icon: Activity,
    title: 'Observable by default',
    body: 'JSON-RPC, scheduling, caching, and resilience emit OpenTelemetry-compatible traces and metrics through System.Diagnostics.',
    href: '/docs/telemetry',
    span: 'lg:col-span-2',
  },
];

function Features() {
  return (
    <section className="border-b border-fd-border py-24">
      <div className="mx-auto w-full max-w-7xl px-6">
        <Reveal className="max-w-2xl">
          <SectionEyebrow>Capabilities</SectionEyebrow>
          <h2 className="mt-4 font-display text-4xl font-semibold tracking-[-0.02em] text-fd-foreground">
            A thin host. A rich application surface.
          </h2>
          <p className="mt-4 text-fd-muted-foreground">
            Auto-detect application patterns, explicitly wire platform capabilities. Everything that
            can be derived from your code is generated for you.
          </p>
        </Reveal>

        <div className="mt-12 grid gap-4 lg:grid-cols-6">
          {features.map((feature, i) => (
            <Reveal key={feature.title} delay={(i % 3) * 80} className={feature.span}>
              <FeatureCard {...feature} />
            </Reveal>
          ))}
        </div>
      </div>
    </section>
  );
}

function FeatureCard({ icon: Icon, title, body, href }: Feature) {
  return (
    <Link href={href} className="panel group flex h-full flex-col p-6">
      <div className="flex size-11 items-center justify-center rounded-xl border border-fd-border bg-fd-muted/40 text-fd-primary transition-colors group-hover:border-azure/50">
        <Icon className="size-5" />
      </div>
      <h3 className="mt-5 font-display text-xl font-semibold tracking-[-0.01em] text-fd-foreground">
        {title}
      </h3>
      <p className="mt-2.5 text-sm leading-relaxed text-fd-muted-foreground">{body}</p>
      <span className="mt-5 inline-flex items-center gap-1.5 text-sm font-medium text-fd-primary opacity-0 transition-opacity group-hover:opacity-100">
        Learn more
        <ArrowRight className="size-3.5" />
      </span>
    </Link>
  );
}

/* ----------------------------------------------------------- Comparison */

const reflectionPoints = [
  'Wiring discovered by scanning the classpath at startup',
  'Missing registration surfaces as a runtime exception',
  'Reflection undermines trimming and AOT',
  'A parallel registration list drifts from your code',
];

const elarionPoints = [
  'Wiring emitted as inspectable DI code at compile time',
  'Missing wiring is a build error, caught in CI',
  'Trim- and AOT-friendly by construction',
  'Intent declared next to the type — one source of truth',
];

function Comparison() {
  return (
    <section className="border-b border-fd-border py-24">
      <div className="mx-auto w-full max-w-7xl px-6">
        <Reveal className="mx-auto max-w-2xl text-center">
          <SectionEyebrow center>Determinism</SectionEyebrow>
          <h2 className="mt-4 font-display text-4xl font-semibold tracking-[-0.02em] text-fd-foreground">
            Build-time, not runtime
          </h2>
        </Reveal>

        <div className="mt-12 grid gap-5 md:grid-cols-2">
          <Reveal>
            <div className="flex h-full flex-col rounded-2xl border border-fd-border bg-fd-muted/20 p-8">
              <span className="chip w-fit border-rose-500/30 text-rose-300/90">
                Reflection scanning
              </span>
              <ul className="mt-6 space-y-4">
                {reflectionPoints.map((p) => (
                  <li key={p} className="flex items-start gap-3 text-sm text-fd-muted-foreground">
                    <XCircle className="mt-0.5 size-4 shrink-0 text-rose-400/80" />
                    {p}
                  </li>
                ))}
              </ul>
            </div>
          </Reveal>

          <Reveal delay={120}>
            <div className="relative flex h-full flex-col overflow-hidden rounded-2xl border border-azure/30 bg-fd-card p-8">
              <div
                aria-hidden
                className="pointer-events-none absolute -top-24 -right-16 size-64 rounded-full bg-azure/15 blur-3xl"
              />
              <span className="chip w-fit border-azure/40 text-aqua">Elarion compile-time</span>
              <ul className="mt-6 space-y-4">
                {elarionPoints.map((p) => (
                  <li key={p} className="flex items-start gap-3 text-sm text-fd-foreground">
                    <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-aqua" />
                    {p}
                  </li>
                ))}
              </ul>
            </div>
          </Reveal>
        </div>
      </div>
    </section>
  );
}

/* ----------------------------------------------------------- Philosophy */

function Philosophy() {
  return (
    <section className="relative isolate overflow-hidden border-b border-fd-border py-28">
      <div aria-hidden className="pointer-events-none absolute inset-0 -z-10 bg-grid opacity-30" />
      <div
        aria-hidden
        className="pointer-events-none absolute left-1/2 top-1/2 -z-10 size-[40rem] -translate-x-1/2 -translate-y-1/2 rounded-full bg-iris/10 blur-[140px]"
      />
      <div className="mx-auto w-full max-w-4xl px-6 text-center">
        <Reveal>
          <p className="eyebrow">The philosophy in one line</p>
          <blockquote className="mt-7 font-display text-3xl font-medium leading-[1.25] tracking-[-0.02em] text-fd-foreground sm:text-4xl lg:text-[2.9rem]">
            “Auto-detect application patterns,{' '}
            <span className="text-gradient">explicitly wire platform capabilities.</span>”
          </blockquote>
          <p className="mx-auto mt-8 max-w-2xl text-fd-muted-foreground">
            Repeating what your code already states — “I handle this request”, “I validate this
            command” — in a separate registration list creates a parallel model that drifts. Elarion
            declares intent next to the type and generates the wiring.
          </p>
          <Link
            href="/docs/philosophy"
            className="group mt-9 inline-flex items-center gap-2 font-medium text-fd-primary"
          >
            Read the full design rationale
            <ArrowRight className="size-4 transition-transform group-hover:translate-x-1" />
          </Link>
        </Reveal>
      </div>
    </section>
  );
}

/* --------------------------------------------------------- Architecture */

const layers = [
  {
    label: 'Your application',
    detail: 'Modules · handlers · [attributes]',
    tone: 'app',
  },
  {
    label: 'Elarion.Generators',
    detail: 'Compile-time wiring · diagnostics · inspectable output',
    tone: 'gen',
  },
  {
    label: 'Elarion runtime',
    detail: 'Decorator pipelines · Result<T> · scheduler · events',
    tone: 'rt',
  },
];

const surfaces = [
  'JSON-RPC',
  'HTTP / REST',
  'MCP tools',
  'EF Core',
  'Blob storage',
  'Paging',
  'Outbox',
  'Telemetry',
];

function Architecture() {
  return (
    <section className="border-b border-fd-border py-24">
      <div className="mx-auto w-full max-w-7xl px-6">
        <Reveal className="max-w-2xl">
          <SectionEyebrow>Architecture</SectionEyebrow>
          <h2 className="mt-4 font-display text-4xl font-semibold tracking-[-0.02em] text-fd-foreground">
            Layered, domain-neutral packages
          </h2>
          <p className="mt-4 text-fd-muted-foreground">
            A clean dependency direction: abstractions at the base, a reusable runtime above, and
            opt-in transports and data layers at the edge. The host stays a thin composition shell.
          </p>
        </Reveal>

        <Reveal delay={100} className="mt-12">
          <div className="mx-auto flex max-w-3xl flex-col gap-3">
            {layers.map((layer) => (
              <div
                key={layer.label}
                className="flex flex-col items-start gap-1 rounded-2xl border border-fd-border bg-fd-card/50 px-6 py-5 sm:flex-row sm:items-center sm:justify-between"
              >
                <span
                  className={
                    layer.tone === 'gen'
                      ? 'text-gradient font-display text-lg font-semibold'
                      : 'font-display text-lg font-semibold text-fd-foreground'
                  }
                >
                  {layer.label}
                </span>
                <span className="font-mono text-xs text-fd-muted-foreground">{layer.detail}</span>
              </div>
            ))}

            <div className="rounded-2xl border border-fd-border bg-fd-muted/20 px-6 py-5">
              <span className="eyebrow">Opt-in transports &amp; data</span>
              <div className="mt-4 flex flex-wrap gap-2">
                {surfaces.map((s) => (
                  <span key={s} className="chip text-fd-foreground/80">
                    {s}
                  </span>
                ))}
              </div>
            </div>
          </div>
        </Reveal>
      </div>
    </section>
  );
}

/* ------------------------------------------------------------ Final CTA */

function FinalCta() {
  return (
    <section className="relative isolate overflow-hidden py-28">
      <div
        aria-hidden
        className="pointer-events-none absolute inset-0 -z-10 bg-gradient-to-b from-transparent via-azure/[0.04] to-iris/[0.06]"
      />
      <div className="mx-auto w-full max-w-3xl px-6 text-center">
        <Reveal>
          <LogoMark className="mx-auto h-12 w-auto drift" />
          <h2 className="mt-7 font-display text-4xl font-semibold tracking-[-0.02em] text-fd-foreground sm:text-5xl">
            Ship your first module in minutes
          </h2>
          <p className="mx-auto mt-5 max-w-xl text-fd-muted-foreground">
            Add the packages, declare a module, write a handler. The generators take care of the
            wiring, the transports, and the typed client.
          </p>
          <div className="mx-auto mt-9 max-w-md">
            <CopyCommand command="dotnet add package Elarion" />
          </div>
          <div className="mt-7 flex flex-wrap items-center justify-center gap-3">
            <Link href="/docs/getting-started/quickstart" className="btn-brand">
              <Terminal className="size-4" />
              Start the quickstart
            </Link>
            <Link href="/docs" className="btn-ghost">
              Browse the docs
            </Link>
          </div>
        </Reveal>
      </div>
    </section>
  );
}

/* -------------------------------------------------------------- Footer */

const footerColumns: { heading: string; links: { label: string; href: string }[] }[] = [
  {
    heading: 'Documentation',
    links: [
      { label: 'Introduction', href: '/docs' },
      { label: 'Getting started', href: '/docs/getting-started/installation' },
      { label: 'Core concepts', href: '/docs/concepts' },
      { label: 'Philosophy', href: '/docs/philosophy' },
    ],
  },
  {
    heading: 'Features',
    links: [
      { label: 'Source generation', href: '/docs/source-generation' },
      { label: 'JSON-RPC & MCP', href: '/docs/json-rpc' },
      { label: 'Scheduling', href: '/docs/scheduling' },
      { label: 'Events', href: '/docs/events' },
    ],
  },
  {
    heading: 'Reference',
    links: [
      { label: 'Packages', href: '/docs/reference/packages' },
      { label: 'Configuration', href: '/docs/reference/configuration' },
      { label: 'vs. ASP.NET Core', href: '/docs/reference/comparison' },
      { label: 'Troubleshooting', href: '/docs/reference/troubleshooting' },
    ],
  },
];

function SiteFooter() {
  return (
    <footer className="border-t border-fd-border bg-fd-muted/20">
      <div className="mx-auto grid w-full max-w-7xl gap-12 px-6 py-16 lg:grid-cols-[1.4fr_1fr_1fr_1fr]">
        <div>
          <LogoMark className="h-8 w-auto" />
          <p className="mt-4 max-w-xs text-sm text-fd-muted-foreground">{appTagline}</p>
          <div className="mt-5 flex items-center gap-3">
            <a
              href={githubUrl}
              target="_blank"
              rel="noreferrer"
              aria-label="GitHub"
              className="flex size-9 items-center justify-center rounded-lg border border-fd-border text-fd-muted-foreground transition-colors hover:border-azure/50 hover:text-fd-foreground"
            >
              <GithubIcon className="size-4" />
            </a>
            <a
              href={`${githubUrl}/blob/main/CHANGELOG.md`}
              target="_blank"
              rel="noreferrer"
              className="flex items-center gap-1.5 rounded-lg border border-fd-border px-3 text-xs text-fd-muted-foreground transition-colors hover:border-azure/50 hover:text-fd-foreground"
            >
              <GitBranch className="size-3.5" />
              Changelog
            </a>
          </div>
        </div>

        {footerColumns.map((column) => (
          <div key={column.heading}>
            <h3 className="font-mono text-xs uppercase tracking-[0.18em] text-fd-muted-foreground">
              {column.heading}
            </h3>
            <ul className="mt-4 space-y-2.5">
              {column.links.map((link) => (
                <li key={link.label}>
                  <Link
                    href={link.href}
                    className="text-sm text-fd-muted-foreground transition-colors hover:text-fd-foreground"
                  >
                    {link.label}
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>

      <div className="rule-fade" />
      <div className="mx-auto flex w-full max-w-7xl flex-col items-center justify-between gap-3 px-6 py-6 text-xs text-fd-muted-foreground sm:flex-row">
        <span className="flex items-center gap-1.5">
          <Sparkles className="size-3.5 text-aqua" />
          Elarion is open source under the MIT License.
        </span>
        <span className="font-mono">© {new Date().getFullYear()} Simon Wimmesberger</span>
      </div>
    </footer>
  );
}

/* -------------------------------------------------------------- shared */

function GithubIcon({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="currentColor"
      aria-hidden
      className={className}
      xmlns="http://www.w3.org/2000/svg"
    >
      <path d="M12 .5C5.73.5.5 5.73.5 12.04c0 5.1 3.29 9.42 7.86 10.95.58.11.79-.25.79-.56 0-.28-.01-1.02-.02-2-3.2.7-3.88-1.55-3.88-1.55-.52-1.34-1.28-1.69-1.28-1.69-1.05-.72.08-.71.08-.71 1.16.08 1.77 1.2 1.77 1.2 1.03 1.78 2.7 1.26 3.36.97.1-.75.4-1.26.73-1.55-2.55-.29-5.24-1.28-5.24-5.69 0-1.26.45-2.28 1.19-3.09-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.18 1.18a11 11 0 0 1 2.9-.39c.98 0 1.97.13 2.9.39 2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.76.11 3.05.74.81 1.19 1.83 1.19 3.09 0 4.42-2.69 5.39-5.25 5.68.41.36.78 1.05.78 2.12 0 1.53-.01 2.77-.01 3.15 0 .31.21.68.8.56A11.55 11.55 0 0 0 23.5 12.04C23.5 5.73 18.27.5 12 .5Z" />
    </svg>
  );
}

function SectionEyebrow({
  children,
  center,
}: {
  children: React.ReactNode;
  center?: boolean;
}) {
  return (
    <span className={`eyebrow flex items-center gap-2 ${center ? 'justify-center' : ''}`}>
      <span className="h-px w-6 bg-azure/60" />
      {children}
    </span>
  );
}
