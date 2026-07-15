import Link from 'next/link';
import { ArrowRight } from 'lucide-react';
import type { Metadata } from 'next';
import { BuildOutput } from './_components/build-output';
import { CodeWindow } from './_components/code-window';
import { MigrationDiffStat } from './_components/diff-stat';
import { CopyCommand } from './_components/copy-button';
import { Code } from './_components/highlight';
import { OutputTabs } from './_components/output-tabs';
import { GithubIcon, Mono, PAD, Section, SectionTitle, Ticks } from './_components/section';
import { githubUrl } from '@/lib/shared';

export const metadata: Metadata = {
  alternates: { canonical: '/' },
};

export default function HomePage() {
  return (
    <main className="flex flex-1 flex-col overflow-x-clip">
      <div className="mx-auto w-full max-w-[80rem] border-x border-(--line)">
        <Hero />
        <FactStrip />
        <AiTeaser />
        <GeneratedOutput />
        <CapabilityIndex />
        <Diagnostics />
        <FieldReport />
        <PhilosophyTeaser />
        <Start />
      </div>
    </main>
  );
}

/* ----------------------------------------------------------------- Hero */

const heroHandler = `
[Handler("clients.get")]
[HttpEndpoint("clients/{id}")]
[RequirePermission("clients", "read")]
public sealed class GetClient(AppDbContext db)
    : IHandler<GetClient.Query, Result<GetClient.Response>> {

    public sealed record Query(Guid Id) : IQuery;
    public sealed record Response(Guid Id, string Name);

    public async ValueTask<Result<Response>> HandleAsync(
        Query q, CancellationToken ct) {
        var client = await db.Clients
            .Where(c => c.Id == q.Id)
            .Select(c => new Response(c.Id, c.Name))
            .FirstOrDefaultAsync(ct);

        return client is null
            ? AppError.NotFound($"Client {q.Id} was not found.")
            : client;
    }
}`;

function Hero() {
  return (
    <section className="relative border-b border-(--line)">
      <Ticks />
      <div className={`grid grid-cols-1 items-center gap-12 py-16 *:min-w-0 lg:grid-cols-[1.02fr_0.98fr] lg:gap-14 lg:py-24 ${PAD}`}>
        <div className="min-w-0">
          <p className="eyebrow">
            <span className="text-(--accent-brand)">///</span> Application framework for .NET
          </p>

          <h1 className="mt-6 font-display text-[2.5rem] font-semibold leading-[1.06] tracking-[-0.03em] text-fd-foreground sm:text-5xl 2xl:text-6xl">
            Write the handler.
            <br />
            The build wires the rest.
          </h1>

          <p className="mt-6 max-w-xl text-lg leading-relaxed text-(--body)">
            One <Mono>[Handler]</Mono> class becomes a JSON-RPC method, a REST endpoint, and an MCP
            tool for AI agents — authorized, validated, and traced by a decorator pipeline that
            Roslyn source generators emit as ordinary C#.{' '}
            <span className="font-medium text-fd-foreground">
              No reflection scanning, no startup discovery, no registration lists to drift.
            </span>
          </p>

          <div className="mt-9 flex flex-wrap items-center gap-3">
            <Link href="/docs/getting-started/quickstart" className="btn-primary">
              Get started
              <ArrowRight className="size-4" />
            </Link>
            <Link href="/philosophy" className="btn-outline">
              The philosophy
            </Link>
          </div>

          <div className="mt-8 w-full max-w-sm">
            <CopyCommand command="dotnet add package Elarion" />
          </div>
        </div>

        <div className="min-w-0">
          <CodeWindow filename="Modules/Clients/GetClient.cs" note="the class you write">
            <Code lang="cs" code={heroHandler} />
          </CodeWindow>
          <p className="mt-3 text-right font-mono text-xs text-fd-muted-foreground">
            what the build does with it → <a href="#generated" className="text-fd-primary hover:underline">01</a>
          </p>
        </div>
      </div>
    </section>
  );
}

/* ------------------------------------------------------------ Fact strip */

const facts = [
  '.NET 10 · C# 14',
  'NativeAOT paths',
  'Zero runtime reflection',
  '40+ focused packages',
  '1,500+ tests',
  'Apache-2.0',
];

function FactStrip() {
  return (
    <section className="border-b border-(--line)">
      <ul className="-ml-px flex flex-wrap">
        {facts.map((fact) => (
          <li
            key={fact}
            className="grow border-l border-(--line-soft) px-5 py-3 text-center font-mono text-xs tracking-wide text-fd-muted-foreground"
          >
            {fact}
          </li>
        ))}
      </ul>
    </section>
  );
}

/* --------------------------------------------------------- AI teaser */

function AiTeaser() {
  return (
    <section className="border-b border-(--line)">
      <Link
        href="/ai"
        className={`group flex flex-col gap-1 py-3.5 transition-colors hover:bg-fd-accent/50 sm:flex-row sm:items-baseline sm:justify-between ${PAD}`}
      >
        <span className="eyebrow">
          <span className="text-(--accent-brand)">///</span> building with AI agents?
        </span>
        <span className="inline-flex items-center gap-2 text-sm font-medium text-fd-foreground">
          The case for Elarion in an AI-first team — in business terms
          <ArrowRight className="size-4 text-fd-primary transition-transform group-hover:translate-x-1" />
        </span>
      </Link>
    </section>
  );
}

/* ------------------------------------------------- 01 · Generated output */

const registrationSample = `
// <auto-generated/> — Elarion.Generators, abridged for reading
public static IServiceCollection AddGetClient(this IServiceCollection services) {
    services.AddScoped<GetClient>();
    services.AddScoped<IHandler<GetClient.Query, Result<GetClient.Response>>>(BuildPipeline);
    return services;
}

private static IHandler<GetClient.Query, Result<GetClient.Response>> BuildPipeline(
    IServiceProvider sp) {
    IHandler<GetClient.Query, Result<GetClient.Response>> handler =
        sp.GetRequiredService<GetClient>();
    handler = new AuthorizationDecorator<GetClient.Query, Result<GetClient.Response>>(handler, …);
    handler = new ObservabilityDecorator<GetClient.Query, Result<GetClient.Response>>(handler, …);
    return handler;
}`;

const httpMapSample = `
// <auto-generated/> — one concrete minimal-API route per [HttpEndpoint], abridged
app.MapGet("clients/{id}",
    static async (
        [AsParameters] GetClient.Query request,
        [FromServices] IHandler<GetClient.Query, Result<GetClient.Response>> handler,
        CancellationToken ct) =>
            ElarionHttpResults.ToResult(await handler.HandleAsync(request, ct)))
    .WithName("GetClient")
    .Produces<GetClient.Response>(200)
    .ProducesElarionErrors();`;

const tsClientSample = `
// generated by @swimmesberger/elarion-jsonrpc-client-generator
import { createRpcApi } from './generated/rpc-client'

const rpc = createRpcApi({ url: '/rpc' })

// dotted method names become typed, Zod-validated calls
const client = await rpc.clients.get({ id })

// tuple-typed batching over a single POST
const [profile, projects] = await rpc.$batch([
  rpc.$request.clients.get({ id }),
  rpc.$request.projects.list({ clientId: id }),
] as const)`;

function GeneratedOutput() {
  return (
    <Section id="generated" n="01" label="Generated output" aside="EmitCompilerGeneratedFiles=true">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <SectionTitle
          title="Attributes in. Ordinary code out."
          lead={
            <>
              The generators run inside <Mono>dotnet build</Mono> and emit the wiring you would
              otherwise write and keep in sync by hand. It is code, not container magic — open it,
              read it, step through it.
            </>
          }
          points={[
            <>DI registrations with the full decorator pipeline, composed in source.</>,
            <>Minimal-API route maps and the shared JSON-RPC + MCP operation registry.</>,
            <>A schema export that becomes a typed TypeScript client.</>,
          ]}
        />

        <OutputTabs
          className="mt-10"
          tabs={[
            {
              label: 'GetClientRegistration.g.cs',
              summary: 'The DI registration — the decorator pipeline composed in source.',
              panel: <Code lang="cs" code={registrationSample} />,
            },
            {
              label: 'ClientsHttp.g.cs',
              summary: 'The [HttpEndpoint] route — GET inferred from the IQuery marker.',
              panel: <Code lang="cs" code={httpMapSample} />,
            },
            {
              label: 'rpc-client.ts',
              summary: 'The exported schema, turned into a typed fetch client with Zod validation.',
              panel: <Code lang="ts" code={tsClientSample} />,
            },
          ]}
        />

        <div className="mt-10 grid gap-8 md:grid-cols-3">
          {[
            {
              n: 'a',
              title: 'Deterministic startup',
              body: 'The host calls generated registrations. Nothing scans assemblies at run time, so boot behavior is fixed at build time.',
            },
            {
              n: 'b',
              title: 'NativeAOT-ready paths',
              body: 'Emitted code is concrete and statically typed, and JSON serialization is source-generated — supported AOT paths avoid runtime discovery.',
            },
            {
              n: 'c',
              title: 'Reviewable like your own code',
              body: 'Flip EmitCompilerGeneratedFiles and the wiring shows up in code review as plain C# diffs.',
            },
          ].map((point) => (
            <div key={point.n} className="border-t border-(--line) pt-4">
              <h3 className="font-medium text-fd-foreground">
                <span className="mr-2 font-mono text-xs text-fd-muted-foreground">{point.n}.</span>
                {point.title}
              </h3>
              <p className="mt-2 text-sm leading-relaxed text-(--body)">{point.body}</p>
            </div>
          ))}
        </div>
      </div>
    </Section>
  );
}

/* ---------------------------------------------- 02 · Capability index */

type Capability = { name: string; desc: string; tag: string; href: string };
type CapabilityGroup = { label: string; caps: Capability[] };

const capabilityGroups: CapabilityGroup[] = [
  {
    label: 'Transports & contracts',
    caps: [
      {
        name: 'JSON-RPC 2.0',
        desc: 'One /rpc endpoint with envelopes, batching, telemetry, and build-time schema export.',
        tag: '[Handler]',
        href: '/docs/capabilities/transports/json-rpc',
      },
      {
        name: 'REST endpoints',
        desc: 'Minimal-API routes with verb inference and RFC 7807 ProblemDetails failures.',
        tag: '[HttpEndpoint]',
        href: '/docs/capabilities/transports/http-endpoints',
      },
      {
        name: 'MCP tools',
        desc: 'The same handlers served to AI agents over Streamable HTTP — one registry, no drift.',
        tag: 'HandlerTransports.Mcp',
        href: '/docs/capabilities/transports/mcp',
      },
      {
        name: 'TypeScript client',
        desc: 'Method contracts, Zod schemas, and a portable fetch client generated from the schema.',
        tag: 'rpc-client.ts',
        href: '/docs/capabilities/transports/typescript-client',
      },
    ],
  },
  {
    label: 'Security',
    caps: [
      {
        name: 'Authorization',
        desc: 'Permissions, roles, claims, and named policies enforced in the pipeline — on every transport.',
        tag: '[RequirePermission]',
        href: '/docs/concepts/authorization',
      },
      {
        name: 'Permission catalog',
        desc: 'Every declared permission aggregated into a typed, queryable catalog at compile time.',
        tag: 'IPermissionCatalog',
        href: '/docs/concepts/authorization',
      },
      {
        name: 'Data-level filters',
        desc: 'Owner, tenant, and grant rules compiled into EF Core predicates — rows filter in SQL, not memory.',
        tag: '[ResourceFilter<T>]',
        href: '/docs/concepts/resource-authorization',
      },
      {
        name: 'Identity',
        desc: 'ASP.NET Core Identity composed onto your plain DbContext; any OIDC/JWT provider works too.',
        tag: '[GenerateElarionIdentity]',
        href: '/docs/capabilities/identity',
      },
    ],
  },
  {
    label: 'Data & persistence',
    caps: [
      {
        name: 'EF Core wiring',
        desc: 'DbSets and configuration application emitted from entity configurations — one source of truth.',
        tag: '[GenerateDbSets]',
        href: '/docs/capabilities/entity-framework',
      },
      {
        name: 'Pagination',
        desc: 'Keyset cursors with provider-aware row-value seeks, plus offset paging over one SortMap.',
        tag: '[Keyset<T>]',
        href: '/docs/capabilities/pagination',
      },
      {
        name: 'Bulk inserts',
        desc: 'Non-tracking inserts over PostgreSQL binary COPY, shaped for high-rate write paths.',
        tag: 'ExecuteInsertAsync',
        href: '/docs/capabilities/bulk-operations',
      },
      {
        name: 'NativeAOT SQL',
        desc: 'Generated row mappers, safe interpolation, and ordered SQL migrations without EF Core.',
        tag: '[SqlRecord]',
        href: '/docs/capabilities/sql-mapping',
      },
      {
        name: 'Blob storage',
        desc: 'Streaming-first store with direct and resumable (tus) uploads and a pending → commit lifecycle.',
        tag: 'IBlobStore',
        href: '/docs/capabilities/blob-storage',
      },
      {
        name: 'Transactions',
        desc: 'One unit-of-work boundary — business writes, events, and idempotency keys commit together.',
        tag: 'IUnitOfWork',
        href: '/docs/concepts/persistence-and-transactions',
      },
    ],
  },
  {
    label: 'Reliability & messaging',
    caps: [
      {
        name: 'Two-plane events',
        desc: 'Domain events commit with the command; integration events deliver after the transaction commits.',
        tag: '[ConsumeEvent]',
        href: '/docs/capabilities/events',
      },
      {
        name: 'Transactional outbox',
        desc: 'At-least-once delivery, recorded in the same transaction as your data — or in-memory for dev.',
        tag: 'Elarion.Messaging.Outbox',
        href: '/docs/capabilities/events/backends',
      },
      {
        name: 'Idempotency',
        desc: 'A database row fences duplicates; a retried command replays its stored result instead of re-running.',
        tag: '[Idempotent]',
        href: '/docs/concepts/idempotency',
      },
      {
        name: 'Resilience',
        desc: 'Named retry/timeout pipelines attach as decorators; the policy catalog stays dependency-light.',
        tag: '[Resilient]',
        href: '/docs/capabilities/resilience',
      },
    ],
  },
  {
    label: 'Realtime & coordination',
    caps: [
      {
        name: 'Client events',
        desc: 'At-most-once hints tell browsers to re-query; reconnect greetings make views converge.',
        tag: 'IClientEvent',
        href: '/docs/capabilities/events/client-events',
      },
      {
        name: 'Ordered streams',
        desc: 'Sequenced hot broadcasts with bounded replay, visible gaps, and resumable SSE.',
        tag: 'StreamHub<T>',
        href: '/docs/capabilities/events/streams',
      },
      {
        name: 'Client connections',
        desc: 'Bidirectional WebSocket and TCP links for devices, interactive input, and server-to-client RPC.',
        tag: 'IClientConnection',
        href: '/docs/capabilities/connections',
      },
      {
        name: 'Device identity',
        desc: 'Single-use pairing, key rotation, and constant-time HMAC handshakes for device links.',
        tag: 'HmacChallengeVerifier',
        href: '/docs/capabilities/devices',
      },
      {
        name: 'Actors',
        desc: 'Mailbox-serialized live state with generated typed facades; use only when a row is not the answer.',
        tag: '[Actor]',
        href: '/docs/concepts/actors',
      },
      {
        name: 'Role leases',
        desc: 'Elect one instance for a coarse role and proxy holder-owned HTTP routes when needed.',
        tag: 'IRoleLease',
        href: '/docs/capabilities/coordination',
      },
      {
        name: 'Data-rate shaping',
        desc: 'Batch loss-tolerant writes and conflate latest-wins values before they hit storage or the UI.',
        tag: 'WriteBehindBuffer<T>',
        href: '/docs/capabilities/data-rate-shaping',
      },
    ],
  },
  {
    label: 'Operations',
    caps: [
      {
        name: 'Caching',
        desc: 'Two-tier HybridCache with a PostgreSQL L2 — reuse the database you already run, skip Redis.',
        tag: '[Cacheable]',
        href: '/docs/capabilities/caching',
      },
      {
        name: 'Feature flags',
        desc: 'Gated handlers return 404 while disabled; variants swap service implementations per request.',
        tag: '[FeatureGate]',
        href: '/docs/concepts/feature-flags',
      },
      {
        name: 'Runtime settings',
        desc: 'Global and per-user settings with optimistic concurrency and live IConfiguration reload.',
        tag: 'ISettingsManager',
        href: '/docs/concepts/settings',
      },
      {
        name: 'Scheduling',
        desc: 'Source-generated jobs with overlap and misfire policy; ${setting} recurrence reschedules live.',
        tag: '[ScheduledJob]',
        href: '/docs/capabilities/scheduling',
      },
      {
        name: 'Telemetry',
        desc: 'OpenTelemetry-compatible traces and metrics from every transport and the scheduler — on by default.',
        tag: 'ActivitySource',
        href: '/docs/capabilities/telemetry',
      },
      {
        name: 'Audit trail',
        desc: 'Explicit durable action records with outcomes, resource context, trace correlation, and optional diffs.',
        tag: '[Auditable]',
        href: '/docs/concepts/auditing',
      },
    ],
  },
];

function CapabilityIndex() {
  return (
    <Section id="capabilities" n="02" label="Capability index" aside="30+ capabilities · 40+ packages">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <SectionTitle
          title="Everything an application needs. Nothing you didn't reference."
          lead={
            <>
              Each capability is a focused package over seams in{' '}
              <Mono>Elarion.Abstractions</Mono>. The core depends on Microsoft.Extensions
              abstractions and nothing else — Polly, HybridCache, and OpenFeature enter your build
              the day a handler asks for them, not before.
            </>
          }
        />
      </div>

      <div>
        {capabilityGroups.map((group) => (
          <div key={group.label}>
            <div className={`border-t border-(--line) bg-fd-muted/40 py-2.5 ${PAD}`}>
              <span className="eyebrow">{group.label}</span>
            </div>
            {group.caps.map((cap) => (
              <Link
                key={cap.name}
                href={cap.href}
                className={`group grid grid-cols-1 gap-x-6 gap-y-1 border-t border-(--line-soft) py-3.5 transition-colors hover:bg-fd-accent/50 md:grid-cols-[12rem_1fr_auto] md:items-baseline ${PAD}`}
              >
                <span className="font-medium text-fd-foreground">{cap.name}</span>
                <span className="text-sm leading-relaxed text-(--body)">{cap.desc}</span>
                <span className="hidden items-baseline gap-3 md:flex">
                  <span className="tag">{cap.tag}</span>
                  <ArrowRight className="size-3.5 shrink-0 self-center text-fd-muted-foreground opacity-0 transition-opacity group-hover:opacity-100" />
                </span>
              </Link>
            ))}
          </div>
        ))}
        <div className={`border-t border-(--line) py-4 ${PAD}`}>
          <Link
            href="/docs/reference/packages"
            className="group inline-flex items-center gap-2 font-mono text-sm text-fd-primary"
          >
            The full package reference
            <ArrowRight className="size-4 transition-transform group-hover:translate-x-1" />
          </Link>
        </div>
      </div>
    </Section>
  );
}

/* --------------------------------------------------- 03 · Diagnostics */

const contrastRows: { scan: string; gen: string }[] = [
  {
    scan: 'Wiring discovered by scanning assemblies at startup',
    gen: 'Wiring emitted as inspectable C# at build time',
  },
  {
    scan: 'A missing registration surfaces as a runtime exception',
    gen: 'Missing wiring is a compile error, caught in CI',
  },
  {
    scan: 'Reflection undermines trimming and native AOT',
    gen: 'Trim- and AOT-friendly by construction',
  },
  {
    scan: 'A parallel registration list drifts from the code',
    gen: 'Intent declared on the type — one source of truth',
  },
];

function Diagnostics() {
  return (
    <Section id="diagnostics" n="03" label="Diagnostics" aside="100+ compile-time checks" tinted>
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <SectionTitle
          title="Wrong wiring doesn't ship."
          lead={
            <>
              What the generators wire, they also validate — every mistake becomes a precise
              diagnostic with a fix direction. Production never finds out.
            </>
          }
          points={[
            <>
              A reach into another module is flagged as you type (<Mono>ELMOD002</Mono>).
            </>,
            <>A route with no inferable verb fails the build, not the demo.</>,
            <>An authorization gate that can&apos;t fail closed is an error, not a hope.</>,
          ]}
        />

        <div className="mt-10 grid items-start gap-10 *:min-w-0 lg:grid-cols-[0.9fr_1.1fr]">
          <div>
            <div className="grid grid-cols-2 gap-x-6 border-b border-(--line) pb-2">
              <span className="eyebrow">Reflection scanning</span>
              <span className="eyebrow text-(--accent-gen)">Elarion</span>
            </div>
            {contrastRows.map((row) => (
              <div
                key={row.gen}
                className="grid grid-cols-2 gap-x-6 border-b border-(--line-soft) py-3.5 text-sm leading-snug"
              >
                <span className="text-fd-muted-foreground">
                  <span className="mr-2 font-mono text-fd-muted-foreground/70">✗</span>
                  {row.scan}
                </span>
                <span className="text-fd-foreground">
                  <span className="mr-2 font-mono text-(--accent-gen)">✓</span>
                  {row.gen}
                </span>
              </div>
            ))}
            <p className="mt-5 text-sm text-fd-muted-foreground">
              Every diagnostic is documented, from <Mono>ELRPC001</Mono> to{' '}
              <Mono>ELMOD002</Mono> —{' '}
              <Link href="/docs/reference/diagnostics" className="text-fd-primary hover:underline">
                see the full list
              </Link>
              .
            </p>
          </div>

          <CodeWindow filename="dotnet build" note="exit code 1">
            <BuildOutput />
          </CodeWindow>
        </div>
      </div>
    </Section>
  );
}

/* -------------------------------------------------- 04 · Field report */

function FieldReport() {
  return (
    <Section id="field-report" n="04" label="Field report" aside="one production migration · real numbers">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <div className="grid items-center gap-10 *:min-w-0 lg:grid-cols-[1fr_0.95fr]">
          <SectionTitle
            title="The first migration deleted 16,223 lines."
            lead={
              <>
                Elarion wasn&apos;t designed on a whiteboard — it was extracted from a production
                application that had grown its own foundation: handler pipeline, transports,
                wiring, caching, the lot. The pull request that moved that application onto the
                released packages added 391 lines and removed 16,223. The same application came
                out the other side, minus its plumbing.
              </>
            }
          />

          <MigrationDiffStat />
        </div>

        <div className="mt-10 grid gap-8 md:grid-cols-3">
          {[
            {
              n: 'a',
              title: 'What the 16,223 were',
              body: 'Bespoke infrastructure — dispatch, registration, caching, retries, auth glue. Code every product rewrites and no product differentiates itself by.',
            },
            {
              n: 'b',
              title: 'What the 391 are',
              body: 'Package references, attributes on the handlers that already existed, and a few registration calls — the declarations the build expands into wiring.',
            },
            {
              n: 'c',
              title: 'Why net −15,832 matters',
              body: 'Every deleted line is one your team no longer reviews, tests, or patches — and one your AI assistants never read, or bill you for, again.',
            },
          ].map((point) => (
            <div key={point.n} className="border-t border-(--line) pt-4">
              <h3 className="font-medium text-fd-foreground">
                <span className="mr-2 font-mono text-xs text-fd-muted-foreground">{point.n}.</span>
                {point.title}
              </h3>
              <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">{point.body}</p>
            </div>
          ))}
        </div>
      </div>
    </Section>
  );
}

/* --------------------------------------------------- Philosophy teaser */

function PhilosophyTeaser() {
  return (
    <section className="border-b border-(--line)">
      <Link
        href="/philosophy"
        className={`group flex flex-col gap-1 py-3.5 transition-colors hover:bg-fd-accent/50 sm:flex-row sm:items-baseline sm:justify-between ${PAD}`}
      >
        <span className="eyebrow">
          <span className="text-(--accent-brand)">///</span> why it&apos;s built this way
        </span>
        <span className="inline-flex items-center gap-2 text-sm font-medium text-fd-foreground">
          The philosophy, for engineers — one maxim, the pipeline, the batteries
          <ArrowRight className="size-4 text-fd-primary transition-transform group-hover:translate-x-1" />
        </span>
      </Link>
    </section>
  );
}

/* --------------------------------------------------------- 05 · Start */

const stepModule = `
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions;
using Elarion.Abstractions.Modules;
using Elarion.AspNetCore;

[assembly: UseElarion]
[assembly: GenerateModuleBootstrapper]

namespace MyApp.System;

[AppModule("System", Kind = AppModuleKind.Core)]
public static partial class SystemModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() =>
        SystemJsonContext.Default;
}

[Handler("system.ping")]
[HttpEndpoint("ping")]
public sealed class Ping : IHandler<Ping.Query, Result<Ping.Response>> {
    public sealed record Query : IQuery;
    public sealed record Response(string Message);

    public ValueTask<Result<Response>> HandleAsync(
        Query query, CancellationToken ct) =>
        ValueTask.FromResult<Result<Response>>(new Response("pong"));
}

[JsonSerializable(typeof(Ping.Query))]
[JsonSerializable(typeof(Ping.Response))]
public sealed partial class SystemJsonContext : JsonSerializerContext;`;

const stepProgram = `
using Elarion.AspNetCore;
using MyApp;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddElarion(builder.Configuration);
builder.Services.AddElarionHttpJson();

var app = builder.Build();

app.MapElarionEndpoints(app.Configuration);

app.Run();`;

const treeRows: { text: string; note?: string }[] = [
  { text: 'MyApp/' },
  { text: '├─ MyApp.csproj', note: 'step 1' },
  { text: '├─ Program.cs', note: 'step 3' },
  { text: '└─ SystemFeature.cs', note: 'step 2' },
];

function ProjectTree() {
  return (
    <div className="code">
      {treeRows.map((row) => (
        <div key={row.text} className="flex items-baseline justify-between gap-6 whitespace-pre leading-[1.9]">
          <span className="text-[#dfe7f6]">{row.text}</span>
          {row.note ? <span className="shrink-0 text-[#64749b]">← {row.note}</span> : null}
        </div>
      ))}
    </div>
  );
}

const buildEmits = [
  'DI registration + observability pipeline',
  'GET /ping — minimal API',
  'system.ping — operation metadata',
  'source-generated JSON metadata',
];

function Start() {
  return (
    <Section id="start" n="05" label="Start" aside="~5 minutes">
      <div className={`py-14 lg:py-16 ${PAD}`}>
        <SectionTitle title="A working handler in three steps." />

        <div className="mt-10 grid gap-8 *:min-w-0 lg:grid-cols-[0.85fr_1.1fr_1.05fr]">
          <div className="flex flex-col">
            <p className="eyebrow">Step 1 — add the packages</p>
            <div className="mt-4 space-y-2">
              <CopyCommand command="dotnet add package Elarion" />
              <CopyCommand command="dotnet add package Elarion.AspNetCore" />
            </div>
            <p className="mt-3 text-sm text-fd-muted-foreground">
              The generators ride along as analyzers — nothing extra to install.
            </p>
            <CodeWindow filename="your project" note="3 files · 1 project" className="mt-5">
              <ProjectTree />
            </CodeWindow>
          </div>
          <div className="flex flex-col">
            <p className="eyebrow">Step 2 — declare one feature</p>
            <CodeWindow filename="SystemFeature.cs" className="mt-4">
              <Code lang="cs" code={stepModule} />
            </CodeWindow>
            <p className="eyebrow mt-6">The build now emits</p>
            <ul className="mt-3 space-y-2.5">
              {buildEmits.map((item) => (
                <li key={item} className="flex items-baseline gap-2.5 text-sm text-(--body)">
                  <span className="font-mono text-(--accent-gen)">✓</span>
                  <span className="font-mono text-[0.8rem]">{item}</span>
                </li>
              ))}
            </ul>
          </div>
          <div className="flex flex-col">
            <p className="eyebrow">Step 3 — wire the host</p>
            <CodeWindow filename="Program.cs" note="the complete host" className="mt-4">
              <Code lang="cs" code={stepProgram} />
            </CodeWindow>
          </div>
        </div>

        <p className="mt-6 max-w-3xl text-sm text-fd-muted-foreground">
          This sample has no hidden database or undeclared dependency: build it and call{' '}
          <Mono>GET /ping</Mono>. The quickstart adds JSON-RPC, while the MCP guide exposes the same
          operation registry to AI agents; the handler and its policy stay unchanged.
        </p>

        <div className="mt-12 flex flex-wrap items-center gap-3">
          <Link href="/docs/getting-started/quickstart" className="btn-primary">
            Open the quickstart
            <ArrowRight className="size-4" />
          </Link>
          <Link href="/docs/tutorial" className="btn-outline">
            Follow the full tutorial
          </Link>
          <a href={githubUrl} target="_blank" rel="noreferrer" className="btn-outline">
            <GithubIcon className="size-4" />
            GitHub
          </a>
        </div>
        <p className="mt-4 text-sm text-fd-muted-foreground">
          The tutorial builds a billing app end to end — modules, authorization, events, and a
          typed React client.
        </p>
      </div>
    </Section>
  );
}
