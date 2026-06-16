# Elarion developer guide

Elarion is an application framework for module-based handler pipelines, compile-time registration, JSON-RPC hosting, scheduled jobs, and optional Entity Framework Core source generation.

The central idea is simple: application assemblies define modules and handlers; host assemblies only wire infrastructure, transport, and deployment concerns.

## Project map

| Project | Purpose | Expected dependencies |
| --- | --- | --- |
| `Elarion` | Handler, result, error, module, pipeline, RPC marker, scheduler, and decorator-list primitives. | Application-level abstractions without provider-specific decorator dependencies. |
| `Elarion.Generators` | Roslyn source generators for handler registration, validator registration, scheduled jobs, RPC method maps, and module bootstrapping. | Analyzer-only reference from application or host projects. |
| `Elarion.JsonRpc` | Transport-neutral JSON-RPC dispatcher, envelopes, result/error types, telemetry, schema export, and RPC method-map trigger. | `System.Text.Json` and Microsoft logging abstractions. |
| `Elarion.AspNetCore` | ASP.NET Core integration: JSON-RPC endpoint mapping, batch execution strategy, current-user integration, and HTTP transport support. | ASP.NET Core abstractions plus `Elarion.JsonRpc`. |
| `Elarion.AspNetCore.SchemaGeneration` | Build-time JSON-RPC schema generation through MSBuild targets and a host-launching tool. | Private build-time package reference from ASP.NET Core host projects. |
| `Elarion.EntityFrameworkCore` | EF Core marker attributes for generated DbSets and entity inclusion. | Marker-only package; consumers provide EF Core. |
| `Elarion.EntityFrameworkCore.Generators` | Roslyn source generator for DbContext DbSet properties and AOT-friendly configuration application. | Analyzer-only reference from EF-consuming projects. |
| `elarion-jsonrpc-client-generator` | TypeScript CLI that turns framework JSON-RPC schema documents into `RpcMethods` types, Zod result schemas, and a portable fetch client. | Node.js/TypeScript tool invoked by frontend applications. |

Anything reusable should move toward the `Elarion` project family. Application-specific domain concepts, database conventions, and infrastructure stay in consuming application projects.

ASP.NET current-user access follows the same boundary: `Elarion` owns the transport-neutral `ICurrentUser` abstraction, while `Elarion.AspNetCore` owns the HTTP integration. Hosts call `AddElarionCurrentUser(...)` during service registration and `UseElarionCurrentUser()` after authentication middleware. The middleware copies claim values into a scoped snapshot, so application handlers do not depend on `HttpContext` or `IHttpContextAccessor`.

## Architecture

Elarion separates three responsibilities.

1. **Application module composition** lives beside the feature code. A module decides which handlers, validators, JSON metadata, and module-owned application services it exposes.
2. **Compile-time discovery** replaces reflection-heavy startup scanning. Generators emit deterministic registration methods from attributes and conventions.
3. **Platform wiring** remains in the API host. The platform owns `WebApplication`, auth, middleware, concrete capability providers, database provider setup, telemetry exporters, and endpoint publication.

This keeps modules independent from the final host while still allowing real platform abstractions where they matter. For example, modules may declare Minimal API endpoints with `IEndpointRouteBuilder` instead of a framework-specific wrapper, so ASP.NET Core endpoint conventions and source generation still work.

## How this differs from normal ASP.NET Core

Elarion is intentionally not a thin wrapper around the default ASP.NET Core style. It chooses a more application-centric architecture where modules and handlers are the source of truth, while the ASP.NET Core host becomes a composition and transport shell.

| Topic | Normal ASP.NET Core style | Elarion style |
| --- | --- | --- |
| Registration | Explicit `services.AddScoped<...>()`, endpoint maps, and feature wiring in `Program.cs` or host extension methods. | Prefer source-generated auto-registration from handler, validator, module, and RPC attributes. Explicit registration remains for infrastructure and app-specific services. |
| Application shape | Controllers, Minimal API route lambdas, or hand-written endpoint classes often become the primary use-case boundary. | `IHandler<TRequest, TResponse>` is the primary use-case boundary; transport adapters call handlers. |
| Modularity | Usually folder/project organization plus manually maintained `AddXyz()` methods. | Modules are first-class with `[AppModule]`, feature flags, dependency ordering, handler aggregation, validator aggregation, endpoint hooks, and JSON metadata hooks. |
| Cross-cutting behavior | Middleware, endpoint filters, MVC filters, MediatR behaviors, or manually nested services. | Generated decorator pipelines wrap handlers in a deterministic order chosen by assembly, module, or handler attributes. |
| Discovery | Runtime reflection scanning is common for validators, handlers, controllers, or endpoint modules. | Compile-time source generation is preferred for deterministic startup, AOT friendliness, and generated code that can be inspected. |
| Host responsibilities | The host often knows every feature and registers most feature services directly. | The host owns platform capabilities, middleware, auth, telemetry, database provider setup, and transports; modules own application composition. |
| Serialization metadata | Usually global JSON options plus reflection fallback. | Modules can contribute `JsonSerializerContext` resolvers so request/response metadata follows the module boundary. |
| EF Core model wiring | Hand-written `DbSet<T>` properties and `ApplyConfigurationsFromAssembly(...)` are common. | Optional EF generator uses interface-first `[GenerateDbSets]`, explicit `[DbEntity]` markers, optional scopes, and direct `IEntityTypeConfiguration<T>` calls. |
| Background work | Individual `BackgroundService` loops usually own their own timers, overlap behavior, logging, and cancellation. | Source-generated scheduled jobs share one in-memory scheduler runtime with typed invocation, explicit overlap policy, `TimeProvider`, and OpenTelemetry instrumentation. |
| Error model | Exceptions, `ProblemDetails`, action results, or ad-hoc response DTOs. | Handlers return `Result<T>` with transport-agnostic `AppError`; the host maps errors to JSON-RPC, HTTP, or another transport. |
| Transport | HTTP REST endpoints are usually the default application API. | JSON-RPC is a first-class optional transport via `Elarion.JsonRpc` plus `Elarion.AspNetCore`, but it stays outside the core framework package. |
| Client contracts | Frontends often hand-write DTOs or call ad-hoc REST client helpers. | JSON-RPC clients consume generated TypeScript/Zod artifacts and a portable fetch client from the exported RPC schema. |

The main tradeoff is that you accept conventions. Handler names, nested `Command`/`Query` and `Response` types, module namespace containment, and pipeline attributes matter because generators use them. In exchange, you get less host boilerplate, inherent modularity, fewer runtime scans, and a clearer separation between application policy and host mechanics.

### Why auto-detection is the default

The framework intentionally prefers **declaring application intent near the type** over maintaining a second registration list elsewhere. A handler already says "I handle this request"; a validator already says "I validate this command"; a service annotated with `[Service]` already says "this is a module service". Repeating the same facts in `Program.cs` or in hand-written `AddXyz()` methods creates a parallel model that can drift from the code it describes.

This is the same broad philosophy used by mature annotation-driven application frameworks: components live below a package or namespace boundary, annotations declare their role, sensible defaults cover the common path, and explicit configuration remains available for exceptional cases. Developers coming from Spring Boot-style Java applications should therefore find the model familiar, even though this framework implements it with .NET source generation rather than runtime classpath scanning.

This is opinionated, but it buys practical advantages:

- **The module owns its application surface.** Adding a handler, validator, JSON context, or service under a module namespace is enough for the module to publish it through generated `Add{Module}...()` methods.
- **The common path is cheap.** Most application services follow the same lifetime, contract, and ownership rules, so the framework optimizes for the repetitive case and leaves explicit code for the unusual case.
- **Registration drift becomes a compile-time problem.** Missing trigger attributes, invalid service contracts, wrong hosted-service lifetimes, and unsupported generic services produce generator diagnostics instead of startup surprises.
- **The host stops knowing feature internals.** The API host wires platform capabilities and transports; it does not need to remember every application handler or module service.
- **Generated code stays inspectable.** The result is still ordinary DI registration code, just emitted deterministically from conventions rather than hidden behind runtime reflection scanning.
- **Refactoring follows structure.** Moving a type between modules changes ownership through namespace containment; deleting a type deletes its registration on the next build.
- **Defaults are bounded by module structure.** Discovery is not "scan everything and hope"; module namespace containment defines which types belong together and which generated registration method owns them.
- **AOT and startup behavior are predictable.** Compile-time discovery avoids broad runtime scans and keeps the dependency graph visible to the compiler and linker.

Explicit registration still has a place when explicitness is the point: database contexts, external clients, provider-specific decorators, authentication, telemetry, and concrete capability providers should be wired by the platform. The framework bias is **auto-detect application patterns, explicitly wire platform capabilities**.

## Core concepts

### Handler

A handler is the primary application use-case unit:

```csharp
using Elarion.Abstractions;

[RpcMethod("clients.get")]
public sealed class GetClient(IClientRepository clients)
    : IHandler<GetClient.Query, Result<GetClient.Response>> {
    public sealed record Query(Guid Id);

    public sealed record Response(Guid Id, string Name);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var client = await clients.FindAsync(query.Id, ct);

        if (client is null) {
            return AppError.NotFound($"Client {query.Id} was not found.");
        }

        return new Response(client.Id, client.Name);
    }
}
```

Handlers should contain business orchestration, not transport concerns. They receive a request object and return a response, usually `Result<T>`.

### Result and AppError

`Result<T>` is a lightweight success-or-failure return type. Success values and `AppError` failures convert implicitly:

```csharp
return new Response(project.Id);
return AppError.Validation("Name is required.");
return AppError.Conflict("Invoice number is already reserved.");
```

`AppError` is transport-agnostic. The API maps it to JSON-RPC errors, HTTP responses, or another protocol-specific shape.

### Module

A module is an application boundary marked with `[AppModule]`:

```csharp
using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.Application.Modules.Clients;

[AppModule("Clients")]
public static partial class ClientsModule {
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration) {
        services.AddClientsHandlers();
        services.AddClientsServices();
        services.AddClientsValidators();
    }

    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() =>
        ClientsJsonContext.Default;
}
```

All module methods are convention-based and optional:

| Method | Called by | Purpose |
| --- | --- | --- |
| `ConfigureServices(IServiceCollection, IConfiguration)` | generated module bootstrapper | Register module handlers, generated module services, validators, and app-level options/configuration. |
| `MapEndpoints(IEndpointRouteBuilder)` | generated module bootstrapper | Declare module-owned Minimal API endpoints. |
| `GetJsonTypeInfoResolver()` | generated module bootstrapper | Contribute source-generated STJ metadata. |

Feature modules are enabled by default and can be disabled with configuration:

```json
{
  "Modules": {
    "Clients": {
      "Enabled": false
    }
  }
}
```

Core modules are required foundation modules and are declared explicitly with `Kind = AppModuleKind.Core`:

```csharp
[AppModule("Core", Kind = AppModuleKind.Core)]
public static partial class CoreModule {
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration) {
        services.AddCoreHandlers();
        services.AddCoreValidators();
    }
}
```

Core modules are always enabled, ignore `Modules:{Name}:Enabled`, and are initialized before feature modules. Do not add `DependsOn = "Core"` just to make a feature module see core services; core availability is implicit from the module kind. `DependsOn` remains for explicit ordering between feature modules or between multiple core modules.

### Validators

Validators use FluentValidation and are grouped by namespace under their module:

```csharp
using FluentValidation;

namespace MyApp.Application.Modules.Clients.Handlers.CreateClient;

public sealed class CreateClientValidator : AbstractValidator<CreateClient.Command> {
    public CreateClientValidator() {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}
```

When `[assembly: UseElarion]` or `[assembly: GenerateModuleValidators]` is present, Elarion emits `Add{ModuleName}Validators()` into the module namespace.

### Services

Annotate service classes with `[Service]` and let the generator emit registration methods:

```csharp
using Elarion.Abstractions;

namespace MyApp.Application.Modules.Clients.Services;

public interface IClientNumberGenerator {
    string Next();
}

[Service(typeof(IClientNumberGenerator))]
public sealed class ClientNumberGenerator : IClientNumberGenerator {
    public string Next() => "C-001";
}
```

Service contract resolution rules:

- If explicit contracts are provided in `[Service(...)]`, those contracts are used.
- If no explicit contracts are provided, directly implemented interfaces are used.
- If no direct interfaces exist, registration falls back to the implementation type itself.
- Default scope is `Scoped`. Override with `Scope = ServiceScope.Singleton` or `Transient`.
- Generic service implementations are intentionally rejected by the generator until open-generic aliasing semantics are defined.

When `[assembly: UseElarion]` or `[assembly: GenerateModuleServices]` is present, Elarion emits `Add{ModuleName}Services()` into the module namespace.

Hosted services are auto-detected when a class implements `IHostedService` or derives from `BackgroundService`. Hosted services must use singleton scope; the generator emits an error for scoped/transient hosted services.

Generated hosted registration follows the standard pattern:

```csharp
services.AddSingleton<IMailboxPollingService, MailboxPollingService>();
services.AddSingleton<IHostedService>(
    sp => (IHostedService)sp.GetRequiredService<IMailboxPollingService>());
```

If you want lifecycle methods hidden from direct service consumers, implement `IHostedService` explicitly:

```csharp
public interface IMailboxPollingService {
    Task PollNowAsync(CancellationToken ct);
}

[Service(typeof(IMailboxPollingService), Scope = ServiceScope.Singleton)]
public sealed class MailboxPollingService : IMailboxPollingService, IHostedService {
    Task IHostedService.StartAsync(CancellationToken ct) {
        // start background loop
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken ct) {
        // stop background loop
        return Task.CompletedTask;
    }

    public Task PollNowAsync(CancellationToken ct) {
        // callable from app code without exposing Start/Stop
        return Task.CompletedTask;
    }
}
```

### Scheduled jobs

Scheduled jobs are in-memory background work owned by one host-local scheduler. They are intended for recurring application tasks and delayed one-off jobs where persistence, dashboards, distributed locks, and cross-process catch-up are not required.

The scheduler uses source-generated descriptors and invocation delegates. There is no runtime assembly scanning, `Type` activation, or reflection-based method invocation. Every run executes through an async DI scope, receives a cancellation token, is tracked during shutdown, and emits scheduler telemetry.

The source-generation surface is implementation-neutral. Application assemblies can reference `Elarion.Abstractions` plus the generator analyzer to use `[ScheduledJob]`, `IScheduledJob<TPayload>`, generated descriptors, and scheduler contracts without referencing the default in-memory scheduler implementation assembly. A host then chooses a runtime by registering `AddInMemoryScheduler(...)` or a custom implementation of `IJobScheduler` / `IJobSchedulerInspector` that consumes the generated `ScheduledJobDescriptor` registrations.

Enable scheduled job generation in the assembly that contains jobs with the full framework opt-in:

```csharp
using Elarion.Abstractions;

[assembly: UseElarion]
```

Register generated job descriptors and the selected scheduler runtime in the host:

```csharp
builder.Services.AddMyAppApplicationScheduledJobs();
builder.Services.AddInMemoryScheduler(builder.Configuration);
```

`AddMyAppApplicationScheduledJobs()` is metadata/descriptor registration only. It does not start a scheduler, register a hosted service, or choose `InMemoryScheduler`. That separation is intentional so another scheduler runtime can use the same generated descriptors.

`AddInMemoryScheduler(IConfiguration)` reads the `Scheduler` section:

```json
{
  "Scheduler": {
    "Enabled": true,
    "MaxConcurrentExecutions": 8,
    "MaxRetainedCompletedJobs": 1024,
    "MaxMisfireCatchUpRuns": 32
  }
}
```

Compile-time recurring jobs are ordinary accessible methods annotated with `[ScheduledJob]`. A scheduled method must be non-generic, return `Task` or `ValueTask`, and accept only optional `IScheduledJobContext` and `CancellationToken` parameters.

```csharp
using Elarion.Abstractions.Scheduling;

namespace MyApp.Application.Modules.Invoicing.Services;

public sealed class RecurringBillingJob(
    IRecurringBillingProcessor processor,
    TimeProvider timeProvider) {
    [ScheduledJob(
        "invoicing.recurringBilling",
        FixedRate = "1d",
        Enabled = "${Modules:Invoicing:Enabled}")]
    public async ValueTask RunAsync(IScheduledJobContext context, CancellationToken ct) {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        await processor.ProcessAllAsync(today, ct);
    }
}
```

The authoring API is intentionally string-based only. Use compact duration literals such as `"1000ms"`, `"1s"`, `"15m"`, `"6h"`, and `"1d"`, or invariant `TimeSpan` text such as `"00:00:01"`. Do not add numeric `TimeUnit`-style variants; string values are the single API because the same property supports literals and configuration placeholders.

Schedule properties:

| Property | Meaning |
| --- | --- |
| `FixedRate` | Grid-aligned interval between due times. Long runs or host pauses skip missed slots instead of producing a catch-up burst. |
| `FixedDelay` | Delay measured from completion of one occurrence to the due time of the next occurrence. This matches polling loops that should wait after each pass. |
| `Cron` | Five-field or six-field cron expression. Six fields include seconds; five-field Unix syntax defaults seconds to `0`. |
| `InitialDelay` | Optional startup delay for `FixedRate` and `FixedDelay`. Not valid with `Cron`. |
| `RunOnStart` | Whether an interval job is due immediately when the host starts. Defaults to `true`; not valid with `Cron`. |
| `TimeZone` | Optional time zone id for cron evaluation. Defaults to UTC. |
| `Enabled` | Boolean literal or placeholder evaluated before every occurrence. Missing placeholder values mean enabled; invalid boolean values disable the occurrence and log an error. |
| `Group` | Optional serialization key for jobs that share an external resource. |
| `Overlap` | What to do when the same job already has an active occurrence. Defaults to `Skip`. |
| `MisfirePolicy` | What to do when a fixed-rate or cron occurrence is so late that later occurrences would also already be due. Defaults to `FireOnce`. |
| `MaxConcurrentRuns` | Job-local cap for concurrent runs when `Overlap = AllowConcurrent`. `0` means no job-local cap; the global scheduler limit still applies. |

Every schedule-bearing method must set exactly one of `FixedRate`, `FixedDelay`, `Cron`, or an `InitialDelay`-only one-time startup schedule:

```csharp
[ScheduledJob(
    "mailbox.poll",
    FixedDelay = "${Mailbox:PollingInterval:-15m}",
    Group = "mailbox",
    Enabled = "${Mailbox:Enabled:-false}",
    Overlap = ScheduledJobOverlap.Skip)]
public async ValueTask PollAsync(CancellationToken ct) {
    await mailboxProcessor.PollAsync(ct);
}

[ScheduledJob(
    "reports.daily",
    Cron = "0 0 3 * * *",
    TimeZone = "Europe/Vienna")]
public async ValueTask RunDailyReportsAsync(CancellationToken ct) {
    await reports.RunDailyAsync(ct);
}

[ScheduledJob("warmup.searchIndex", InitialDelay = "10s")]
public async ValueTask WarmSearchIndexAsync(CancellationToken ct) {
    await searchIndex.WarmAsync(ct);
}
```

Use `Cron = "-"` to disable a cron trigger, matching Spring's disabled cron sentinel. This is most useful with placeholders:

```csharp
[ScheduledJob("reports.daily", Cron = "${Reports:DailyCron:--}")]
public async ValueTask RunDailyReportsAsync(CancellationToken ct) {
    await reports.RunDailyAsync(ct);
}
```

Configuration placeholders use Spring-style syntax:

| Placeholder | Behavior |
| --- | --- |
| `${Jobs:Interval}` | Resolve `Jobs:Interval`; throw during schedule resolution if it is not configured. |
| `${Jobs:Interval:-15m}` | Resolve `Jobs:Interval`; use `15m` when it is not configured or blank. |

Placeholders are re-resolved for every recurring occurrence. If a previously valid schedule later becomes invalid, the scheduler logs the error and keeps the last valid schedule so the recurring chain does not die while configuration is being fixed.

Runtime-created one-off jobs use `IScheduledJob<TPayload>` and the typed `IJobScheduler` API. The job type still has `[ScheduledJob]` so the generator can emit its descriptor and direct invocation delegate, but it usually omits a recurring schedule.

```csharp
using Elarion.Abstractions.Scheduling;

[ScheduledJob("emails.send")]
public sealed class SendEmailJob(IEmailSender sender) : IScheduledJob<SendEmailPayload> {
    public async ValueTask ExecuteAsync(
        SendEmailPayload payload,
        IScheduledJobContext context,
        CancellationToken ct) {
        await sender.SendAsync(payload.To, payload.Subject, payload.Body, ct);
    }
}

public sealed record SendEmailPayload {
    public required string To { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
}
```

```csharp
var handle = await scheduler.ScheduleAsync<SendEmailJob, SendEmailPayload>(
    new SendEmailPayload {
        To = "customer@example.com",
        Subject = "Invoice",
        Body = "Your invoice is ready."
    },
    timeProvider.GetUtcNow().AddMinutes(5),
    ct);

await scheduler.CancelJobAsync(handle.JobId, ct);
```

When a handler enqueues background work and needs to return an operation id, return `handle.JobId`. That is also the id callers pass back to `CancelJobAsync`. `RunId` identifies one scheduler attempt for diagnostics and snapshots; it can change across deferred retries.

Overlap behavior is explicit:

| Policy | Behavior |
| --- | --- |
| `Skip` | Drop a recurring occurrence when the same job is already running. |
| `Queue` | Serialize occurrences of the same job; recurring queued occurrences coalesce so slow jobs do not pile up unlimited waiters. |
| `AllowConcurrent` | Allow multiple occurrences of the same job to run at the same time, still subject to global `MaxConcurrentExecutions`. |

Use `Group` when different jobs must not overlap with each other, for example because they use the same mailbox, report export directory, or third-party API quota. Use `MaxConcurrentRuns` with `AllowConcurrent` when a single job may overlap but should still be bounded:

```csharp
[ScheduledJob(
    "imports.customerSync",
    FixedRate = "30s",
    Overlap = ScheduledJobOverlap.AllowConcurrent,
    MaxConcurrentRuns = 2)]
public async ValueTask SyncCustomersAsync(CancellationToken ct) {
    await importer.SyncAsync(ct);
}
```

Misfire behavior is separate from overlap behavior. It only applies to recurring grid schedules (`FixedRate` and `Cron`) when the scheduler observes a due occurrence after one or more later occurrences would also already be due, for example after an in-process pause or a large clock jump. Small timer jitter is not treated as a misfire. `FixedDelay` schedules do not have missed grid slots because the next due time is calculated after completion.

| Policy | Behavior |
| --- | --- |
| `FireOnce` | Preserve the default behavior: run one overdue occurrence, then schedule the next future occurrence and skip intermediate slots. |
| `Skip` | Do not run the stale overdue occurrence. Record a skipped outcome with reason `misfire`, then schedule the next future occurrence. |
| `CatchUp` | Run missed occurrences in due-time order, respecting normal overlap/concurrency rules. Catch-up is bounded by `Scheduler:MaxMisfireCatchUpRuns`; after the cap, the scheduler coalesces to the next future occurrence. |

Runtime one-off jobs scheduled through `IJobScheduler` do not use recurring misfire policy. A one-off job with a due time in the past runs as soon as scheduler capacity is available.

The scheduler exposes lightweight state through `IJobSchedulerInspector`:

```csharp
var snapshot = schedulerInspector.GetSnapshot();
var activeRuns = snapshot.ActiveRuns;
var nextDue = snapshot.Jobs.Single(job => job.Name == "reports.daily").NextDueTimeUtc;

var job = schedulerInspector.GetJob(handle.JobId);
```

`NextDueTimeUtc` is the next queued due time known to the running in-memory scheduler. This is enough for a dashboard to show the next fire time for enabled recurring jobs without a separate trigger API. Runtime jobs with deferred retry expose their next retry due time through `ScheduledJobState.NextAttemptDueTimeUtc`.

Generated scheduled-job registries also emit job references so admin/manual-run code does not repeat string literals:

```csharp
var jobName = MyAppApplicationScheduledJobRegistrationJobReferences.ReportsDaily.Name;
```

`IJobScheduler.CancelJobAsync(jobId, ct)` targets the logical runtime job and is the user-facing cancellation API. It cancels queued jobs, waiting retry attempts, or the currently active attempt without the caller needing to know the job's current state.

`IJobScheduler.CancelRunAsync(runId, ct)` targets one concrete attempt. Use it for operator or diagnostic workflows that act on a specific run shown in a scheduler snapshot. If deferred retry schedules another attempt, that later attempt has a different `RunId` but keeps the same `JobId`.

Job code can also request cancellation through `IScheduledJobContext.RequestCancellation()`.

### Resilience policies

Resilience is a framework-level capability expressed as neutral policy metadata plus an `IResiliencePipelineRunner` contract. Application code uses Elarion attributes and generated registrations; the default runtime is backed by `Microsoft.Extensions.Resilience`/Polly, but that is an explicit host choice and not required by the attributes or generated metadata.

Enable resilience policy generation with the full framework opt-in or `[assembly: GenerateResiliencePolicies]`, then define named policies as partial static classes:

```csharp
using Elarion.Abstractions.Resilience;

[ResiliencePolicy(
    "invoice-email",
    MaxRetryAttempts = 4,
    Delay = "10s",
    Backoff = ResilienceBackoffType.Exponential,
    MaxDelay = "5m",
    UseJitter = true,
    Timeout = "30s")]
public static partial class InvoiceEmailPolicy;
```

Policy properties are behavioral, not just metadata:

| Property | Runtime meaning |
| --- | --- |
| `MaxRetryAttempts` | Number of retries after the original attempt. `4` means up to 5 total attempts. Supplying this or any other retry property enables retry generation. |
| `Delay` | Base delay before the next retry. In inline mode this delay is awaited inside the current call/run; in deferred scheduler mode it becomes the next attempt's due time. |
| `Backoff` | Delay growth: `Constant` reuses `Delay`, `Linear` multiplies by attempt number, `Exponential` doubles from the base delay. |
| `MaxDelay` | Optional cap for calculated retry delays. |
| `UseJitter` | Adds randomization to retry delays to avoid many jobs retrying at the same instant. |
| `Timeout` | Per-attempt timeout. It limits one try, not the whole policy execution across all retries. |

`Timeout = "30s"` means "each attempt may run for at most 30 seconds." With `MaxRetryAttempts = 4`, the operation may therefore run for more than 30 seconds in total because each retry receives its own 30-second attempt window plus retry delays. Use a caller/host cancellation token or an outer application deadline when you need a total end-to-end deadline.

Timeouts are cooperative. When the timeout fires, the framework cancels the attempt token and records the attempt as failed/timed out; handler and job code must pass that token into database calls, HTTP calls, delays, and other async work so the underlying operation stops promptly.

The generator emits the policy name, a typed `Reference`, a per-policy registration method, and an assembly aggregation method. Generated policy registration stores neutral `ResiliencePolicyMetadataRegistration` instances only; it does not call `AddResiliencePipeline(...)` or register the default runner. Register generated policies and the selected runtime during startup:

```csharp
builder.Services.AddMyAppApplicationResiliencePolicies();
builder.Services.AddMicrosoftResilienceRuntime();
```

`AddMicrosoftResilienceRuntime()` is the default implementation adapter. It consumes generated metadata and lazily builds executable Microsoft/Polly pipelines. A custom runtime can instead register its own `IResiliencePipelineRunner` and `IResiliencePolicyCatalog` while still using the same attributes and generated metadata.

Handlers opt into request-path resilience with `[Resilient]`:

```csharp
[Resilient(InvoiceEmailPolicy.Name)]
public sealed class SendInvoiceEmail
    : IHandler<SendInvoiceEmail.Command, Result<SendInvoiceEmail.Response>> {
    // ...
}
```

Use handler resilience only for idempotent work where the caller should wait for all retry attempts. The generated `ResilienceDecorator<TRequest,TResponse>` wraps the existing handler pipeline so each retry attempt can execute through the normal decorators.

Handler example with the policy above:

1. The handler starts attempt 1.
2. If attempt 1 throws an ordinary exception, the retry policy waits according to `Delay`/`Backoff`.
3. Attempt 2 starts with a fresh timeout window.
4. The caller waits until an attempt succeeds, retry attempts are exhausted, cancellation is requested, or a non-retryable failure is thrown.

`OperationCanceledException` and `NonRetryableException` are terminal and are not retried. `Result<T>` failures are also terminal because they are normal return values, not exceptions.

Scheduled jobs can also opt into inline resilience:

```csharp
[Resilient(InvoiceEmailPolicy.Name)]
[ScheduledJob("invoice-email.retryOutbox", FixedDelay = "1m")]
public async ValueTask RetryOutboxAsync(CancellationToken ct) {
    await outbox.SendPendingAsync(ct);
}
```

Inline resilience behaves like Spring-style composition: retries happen inside the current scheduler run. `RunId` and scheduler status still represent one scheduled occurrence.

For inline scheduled jobs, retry delay and timeout behave like handler resilience: the current scheduler run remains active while retry delays are awaited. This is right for short idempotent work where one occurrence owns all attempts. It is not ideal for long background operations that should show `WaitingRetry` between attempts.

For runtime-created one-off jobs where another handler needs to observe status, use scheduler-deferred retry with the same generated policy reference:

```csharp
var handle = await scheduler.EnqueueAsync<SendEmailJob, SendEmailPayload>(
    payload,
    new ScheduledJobOptions {
        ResiliencePolicy = InvoiceEmailPolicy.Reference,
        ResilienceMode = ScheduledJobResilienceMode.DeferredRetry,
        CorrelationId = payload.InvoiceId.ToString()
    },
    ct);

return new QueueEmailResponse(handle.JobId);
```

Deferred retry releases scheduler concurrency between attempts. A failed attempt records `WaitingRetry`, calculates the next due time from the generated retry metadata, and enqueues a fresh attempt with a new `RunId` and the same `JobId`. Terminal states are `Succeeded`, `Failed`, `Cancelled`, or `Skipped` and are retained in memory up to `Scheduler:MaxRetainedCompletedJobs`.

For deferred runtime jobs, `Timeout` is still per attempt:

1. Attempt 1 starts with its own `RunId`.
2. If the attempt throws or times out, the scheduler records that attempt outcome.
3. If retries remain, the logical job state becomes `WaitingRetry` and `NextAttemptDueTimeUtc` is set from the policy delay/backoff.
4. At the retry due time, the scheduler starts attempt 2 with a new `RunId`, the same `JobId`, and a fresh timeout window.

Deferred retry requires generated policy metadata because the scheduler needs framework-owned retry settings to calculate future due times without sleeping inside an executing resilience pipeline.

Status handlers can inspect the logical job:

```csharp
var state = schedulerInspector.GetJob(jobId);
if (state is null) {
    return AppError.NotFound("Job state is no longer available.");
}

return new JobStatusResponse(
    state.Status,
    state.Attempt,
    state.MaxAttempts,
    state.NextAttemptDueTimeUtc,
    state.LastError);
```

Deferred retry intentionally stays in-memory. Missing state can mean the id was never known, the process restarted, or a terminal state aged out. Use durable infrastructure if retry history must survive process restarts.

Operational constraints:

- The scheduler is in-memory only. Queued runtime jobs and due recurring state are lost when the process stops.
- Fixed-rate and cron schedules skip missed in-process slots instead of replaying a burst.
- There is no global one-second polling loop. The runtime waits until the nearest due item and wakes early when a new earlier item is enqueued.
- `TimeProvider` is used throughout, so tests can drive millisecond schedules deterministically with a fake clock. Production precision is still bounded by the operating system timer resolution and normal host load.
- Scheduler telemetry is emitted through `SchedulerTelemetry.Source` and `SchedulerTelemetry.Meter`; hosts that register the framework telemetry source/meter receive scheduling/enqueue/cancel spans, execution spans, job duration, lag, count, active-run, failure, skipped, retry, and cancellation signals.

### Decorator pipelines

Elarion registers handlers through generated factories that wrap the handler in an ordered decorator pipeline. Pipeline attributes can be applied at three scopes, from least to most specific:

1. Assembly
2. Module class
3. Handler class

The most specific pipeline wins.

The framework does not ship concrete decorators or default pipeline presets. Decorators usually depend on app choices such as logging, validation, database transactions, idempotency, tenancy, or retry policy. Define those in your application, then expose named pipeline attributes with `DecoratorListAttribute`.

Example pipeline presets:

```csharp
using MyApp.Application.Decorators;
using Elarion.Abstractions.Pipeline;

namespace MyApp.Application.Pipeline;

[DecoratorList(
    typeof(TransactionDecorator<,>),
    typeof(DbConstraintDecorator<,>),
    typeof(ValidationDecorator<,>))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
public sealed class DefaultPipelineAttribute : Attribute;

[DecoratorList(typeof(ValidationDecorator<,>))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
public sealed class ReadOnlyPipelineAttribute : Attribute;
```

Then apply the application-owned presets:

```csharp
using MyApp.Application.Pipeline;
using Elarion.Abstractions;

[assembly: DefaultPipeline]
[assembly: GenerateModuleHandlers]
[assembly: GenerateModuleServices]
[assembly: GenerateModuleValidators]
```

Handler override:

```csharp
[ReadOnlyPipeline]
[RpcMethod("clients.search")]
public sealed class SearchClients
    : IHandler<SearchClients.Query, Result<SearchClients.Response>> {
    // ...
}
```

Decorators must implement `IHandler<TRequest, TResponse>` and accept the inner handler as a constructor parameter. Any additional constructor parameters are resolved from DI by the generated factory.

Minimal logging decorator example:

```csharp
using Microsoft.Extensions.Logging;
using Elarion.Abstractions;

namespace MyApp.Application.Decorators;

public sealed class LoggingDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    ILogger<LoggingDecorator<TRequest, TResponse>> logger
) : IHandler<TRequest, TResponse> {
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        logger.LogDebug("Handling {RequestType}", typeof(TRequest).Name);
        var response = await inner.HandleAsync(request, ct);
        logger.LogDebug("Handled {RequestType}", typeof(TRequest).Name);

        return response;
    }
}
```

Validation decorator example:

```csharp
using System.Reflection;
using FluentValidation;
using Elarion.Abstractions;

namespace MyApp.Application.Decorators;

public sealed class ValidationDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IEnumerable<IValidator<TRequest>> validators
) : IHandler<TRequest, TResponse> {
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var failures = new List<string>();

        foreach (var validator in validators) {
            var result = await validator.ValidateAsync(request, ct);
            failures.AddRange(result.Errors.Select(error => error.ErrorMessage));
        }

        if (failures.Count == 0) {
            return await inner.HandleAsync(request, ct);
        }

        return ResultFactory.Failure<TResponse>(
            AppError.Validation(string.Join("; ", failures), failures));
    }
}
```

Generic decorators that need to turn an `AppError` into `TResponse` can keep that helper local to the application:

```csharp
using System.Reflection;
using Elarion.Abstractions;

namespace MyApp.Application.Decorators;

internal static class ResultFactory {
    public static TResponse Failure<TResponse>(AppError error) {
        var responseType = typeof(TResponse);
        if (responseType.IsGenericType &&
            responseType.GetGenericTypeDefinition() == typeof(Result<>)) {
            var failureMethod = responseType.GetMethod(
                nameof(Result<object>.Failure),
                BindingFlags.Static | BindingFlags.Public)!;

            return (TResponse)failureMethod.Invoke(null, [error])!;
        }

        throw new InvalidOperationException(
            $"Cannot map AppError to {responseType.Name}. Handler must return Result<T>.");
    }
}
```

Transaction decorator example:

```csharp
using Microsoft.EntityFrameworkCore;
using Elarion.Abstractions;

namespace MyApp.Application.Decorators;

public sealed class TransactionDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    DbContext db
) : IHandler<TRequest, TResponse> {
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var response = await inner.HandleAsync(request, ct);

        if (response is IResultLike { IsSuccess: true }) {
            await transaction.CommitAsync(ct);
        } else {
            await transaction.RollbackAsync(ct);
        }

        return response;
    }
}
```

`ResultFactory.Failure<TResponse>` is intentionally not part of the framework API today. Applications that need generic failure construction should keep that mapping local to their decorator implementations until the framework has a stable abstraction for it.

## Source generation setup

Application projects reference the core primitives and the generator analyzer:

```xml
<ItemGroup>
  <ProjectReference Include="..\Elarion\Elarion.csproj" />
  <ProjectReference Include="..\Elarion.Generators\Elarion.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Then add the full framework assembly trigger:

```csharp
using Elarion.Abstractions;
using MyApp.Application.Pipeline;

[assembly: DefaultPipeline]
[assembly: UseElarion]
```

`UseElarion` enables the framework-owned assembly-level generators for module handlers, module services, module validators, and scheduled jobs. It intentionally does not replace application-owned policy attributes such as `DefaultPipeline`, and it does not replace class/interface-targeted triggers such as `[GenerateRpcMethodMap]`, `[GenerateModuleBootstrapper]`, or `[GenerateDbSets]`.

Use the narrower trigger attributes only when an assembly intentionally wants a subset:

```csharp
[assembly: GenerateModuleHandlers]
[assembly: GenerateModuleServices]
[assembly: GenerateModuleValidators]
[assembly: GenerateScheduledJobs]
```

The generators emit:

| Generator | Trigger | Generated API |
| --- | --- | --- |
| `HandlerRegistrationGenerator` | `[assembly: UseElarion]` or `[assembly: GenerateModuleHandlers]` | `Add{HandlerName}()` and `Add{ModuleName}Handlers()` extension methods. |
| `ModuleServiceRegistrationGenerator` | `[assembly: UseElarion]` or `[assembly: GenerateModuleServices]` | `Add{ServiceName}Service()` and `Add{ModuleName}Services()` extension methods. |
| `ModuleValidatorRegistrationGenerator` | `[assembly: UseElarion]` or `[assembly: GenerateModuleValidators]` | `Add{ModuleName}Validators()` extension methods. |
| `SchedulerRegistrationGenerator` | `[assembly: UseElarion]` or `[assembly: GenerateScheduledJobs]` | `Add{AssemblyName}ScheduledJobs()` extension methods that register descriptors and job types. |
| `RpcMethodMapGenerator` | `[GenerateRpcMethodMap]` on a host partial class | `RpcMethodMap.RegisterAll(dispatcher)`. |
| `AppModuleDiscoveryGenerator` | `[GenerateModuleBootstrapper]` on a host partial class | `ConfigureAllServices`, `MapAllEndpoints`, `GetAllJsonTypeInfoResolvers`, `IsModuleEnabled`, `GetAllModuleNames`. |

Generated code intentionally uses explicit type names and DI factory registrations. Startup behavior remains deterministic and AOT-friendly.

## Host setup

The host references the application, `Elarion.JsonRpc`, `Elarion.AspNetCore`, and the generator analyzer:

```xml
<ItemGroup>
  <ProjectReference Include="..\Elarion.JsonRpc\Elarion.JsonRpc.csproj" />
  <ProjectReference Include="..\Elarion.AspNetCore\Elarion.AspNetCore.csproj" />
  <ProjectReference Include="..\MyApp.Application\MyApp.Application.csproj" />
  <ProjectReference Include="..\Elarion.Generators\Elarion.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Create a module bootstrapper partial:

```csharp
using Elarion.AspNetCore;

namespace MyApp.Api.Hosting;

[GenerateModuleBootstrapper]
public static partial class ModuleBootstrapper;
```

Create an RPC method map partial:

```csharp
using Elarion.JsonRpc;

namespace MyApp.Api.Rpc;

[GenerateRpcMethodMap]
public static partial class RpcMethodMap {
    public static partial JsonRpcDispatcher RegisterAll(JsonRpcDispatcher dispatcher);
}
```

Wire the host:

```csharp
var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddDbContext<AppDbContext>(/* provider setup */);
builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<AppDbContext>());

builder.Services.AddMyAppApplicationScheduledJobs();
builder.Services.AddMyAppApplicationResiliencePolicies();
builder.Services.AddInMemoryScheduler(builder.Configuration);
builder.AddMyAppPlatformCapabilities();
ModuleBootstrapper.ConfigureAllServices(builder.Services, builder.Configuration);

var serializerOptions = CreateSerializerOptions(builder.Configuration);
builder.Services.AddSingleton(serializerOptions);
builder.Services.AddJsonRpc(o => o.SerializerOptions = serializerOptions);

var frozenDispatcher = RpcMethodMap
    .RegisterAll(new JsonRpcDispatcher(serializerOptions))
    .Freeze();

builder.Services.AddSingleton(frozenDispatcher);

var app = builder.Build();

ModuleBootstrapper.MapAllEndpoints(app, app.Configuration);
app.MapJsonRpc();

app.Run();
```

The host still owns:

- authentication and authorization policy setup
- database provider configuration and migrations
- concrete platform capability providers
- middleware order
- health checks, telemetry exporters, and deployment integration
- JSON-RPC endpoint publication and transport-specific error mapping

## Telemetry and tracing

Elarion emits OpenTelemetry-compatible signals through `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics`. Runtime packages do not depend on the OpenTelemetry SDK; the host chooses exporters and registers the sources/meters it wants to collect.

Register the framework sources/meters from the host:

```csharp
using Elarion.AspNetCore;
using Elarion.Abstractions.Scheduling;
using Elarion.Caching;
using Elarion.Resilience;

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(
            JsonRpcTelemetry.ActivitySourceName,
            SchedulerTelemetry.ActivitySourceName,
            HandlerCacheTelemetry.ActivitySourceName,
            ResilienceTelemetry.ActivitySourceName)
        /* add exporters */)
    .WithMetrics(metrics => metrics
        .AddMeter(
            JsonRpcTelemetry.MeterName,
            SchedulerTelemetry.MeterName,
            HandlerCacheTelemetry.MeterName,
            ResilienceTelemetry.MeterName)
        /* add exporters */);
```

Current first-party telemetry surfaces:

| Surface | Source/meter | Trace coverage |
| --- | --- | --- |
| JSON-RPC | `JsonRpcTelemetry` (`JsonRpc`) | Every request dispatch creates a span, including single calls, notifications, batch items, invalid protocol versions, unknown methods, invalid params, application errors, unhandled exceptions, invalid envelopes, parse errors, and batch-level failures. Registered methods use their canonical method name; invalid or unregistered methods use bounded sentinels to avoid unbounded metric cardinality. Spans include bounded tags such as method, response/error code, JSON-RPC version, and batch index/size. |
| Scheduler | `SchedulerTelemetry` (`Elarion.Scheduling`) | Runtime schedule/enqueue/cancel operations and job executions create spans. Runtime-scheduled jobs preserve scheduling trace context into the later execution span when possible. Recurring fixed-rate, fixed-delay, cron, skipped, misfired/coalesced, retry, cancellation, and failure outcomes are trace-visible. |
| Handler cache | `HandlerCacheTelemetry` (`Elarion.Caching`) | Cache get/create spans expose the precise observable outcome: `miss-factory-executed`, `miss-non-cacheable`, or `cached-or-coalesced` when `HybridCache` returns without this call running the factory. Factory execution events, payload policy errors, and invalidation spans are trace-visible. Tags avoid full keys, raw user ids, request values, and arbitrary tag values. |
| Resilience | `ResilienceTelemetry` (`Elarion.Resilience`) | Named policy execution spans expose final outcome and duration. Retry and timeout callbacks add span events when the default Microsoft/Polly-backed runtime performs those actions. |

Elarion intentionally does not add blanket spans around every generated handler invocation by default. JSON-RPC, scheduler, cache, and resilience spans already identify the framework-owned runtime boundaries, and unconditional handler spans can duplicate transport spans or add cardinality before a host has decided its tracing model. Applications that need handler-level spans can add an explicit decorator through the generated pipeline system.

The TypeScript JSON-RPC client remains OpenTelemetry-package-free and frontend-framework-free. Browser or Node.js applications should wrap the generated transport/fetch boundary when they need client-side tracing.

## JSON-RPC client generation

The framework JSON-RPC pipeline is intentionally end-to-end:

1. Application handlers declare `[RpcMethod("module.action")]`.
2. `RpcMethodMapGenerator` emits the dispatcher registration map.
3. The host configures a `JsonRpcDispatcher` with the same `JsonSerializerOptions` used at runtime.
4. `Elarion.JsonRpc.JsonRpcSchemaExporter` or `Elarion.AspNetCore.SchemaGeneration` exports `rpc-schema.json` from the registered dispatcher.
5. `elarion-jsonrpc-client-generator` converts that schema into frontend TypeScript/Zod artifacts and a typed fetch client.
6. The frontend can use the generated client directly or wrap it in framework-specific server functions/cache hooks.

### Build-time JSON-RPC schema generation

Host projects can generate `rpc-schema.json` during `dotnet build` by adding the schema generation package as private build tooling:

```xml
<ItemGroup>
  <PackageReference Include="Elarion.AspNetCore.SchemaGeneration"
                    Version="0.1.0"
                    PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <ElarionJsonRpcGenerateSchema>true</ElarionJsonRpcGenerateSchema>
  <ElarionJsonRpcSchemaOutputPath>$(MSBuildProjectDirectory)/../../rpc-schema.json</ElarionJsonRpcSchemaOutputPath>
</PropertyGroup>
```

The package imports MSBuild targets that run after the host project is compiled. The target launches the built application, captures the host immediately after `builder.Build()`, resolves the registered `JsonRpcDispatcher`, and writes the schema using the dispatcher's runtime serializer options. Application code after `builder.Build()` does not run during generation.

Useful properties:

| Property | Default | Purpose |
| --- | --- | --- |
| `ElarionJsonRpcGenerateSchema` | `false` | Enables the package targets. |
| `ElarionJsonRpcGenerateSchemaOnBuild` | Same as `ElarionJsonRpcGenerateSchema` | Controls automatic generation during `dotnet build`. |
| `ElarionJsonRpcSchemaOutputPath` | `$(BaseIntermediateOutputPath)rpc-schema.json` | Exact schema file path. |
| `ElarionJsonRpcSchemaOutputDirectory` | `$(BaseIntermediateOutputPath)` | Used with `ElarionJsonRpcSchemaFileName` when no explicit output path is set. |
| `ElarionJsonRpcSchemaFileName` | `rpc-schema.json` | File name used with the output directory. |
| `ElarionJsonRpcSchemaEnvironment` | `Development` | Value used for `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` while loading the app. |
| `ElarionJsonRpcSchemaApplicationArguments` | Empty | Optional arguments passed to the app entry point after `--`. |
| `ElarionJsonRpcSchemaGenerationOptions` | Empty | Extra tool arguments for advanced scenarios. |

Manual generation uses the same target:

```bash
dotnet msbuild src/MyApp.Api/MyApp.Api.csproj \
  -t:GenerateElarionJsonRpcSchema \
  -p:ElarionJsonRpcGenerateSchema=true
```

Because the application is loaded during the build, expensive or external startup work should be guarded when needed. `JsonRpcSchemaGeneration.IsRunning` checks the Elarion marker environment variable and the schema generation tool entry assembly name:

```csharp
var app = builder.Build();

if (!JsonRpcSchemaGeneration.IsRunning) {
    await app.Services.GetRequiredService<IStartupTask>().RunAsync();
}
```

Schema generation requires the host to register a frozen `JsonRpcDispatcher` before `builder.Build()`. If the dispatcher is missing, unfrozen, or empty, the build target fails with an actionable error instead of writing a stale schema.

The client generator is framework-owned because it interprets the framework schema format. It emits a generated API surface where dotted JSON-RPC method names become nested methods, for example `clients.get` becomes `rpc.clients.get(...)`. The generated runtime stays portable: it uses browser/Node.js `fetch`, accepts injected `fetch`, supports headers and `AbortSignal`, validates results through generated Zod schemas, and exposes JSON-RPC batching.

For example, applications can invoke the published CLI:

```bash
npx @swimmesberger/elarion-jsonrpc-client-generator \
  --schema rpc-schema.json \
  --out src/generated
```

The generator emits:

| File | Purpose |
| --- | --- |
| `rpc-types.ts` | `RpcMethods` interface mapping method names to params/result types. |
| `rpc-schemas.ts` | `rpcResultSchemas` Zod map for runtime result validation. |
| `rpc-client.ts` | Typed browser/Node.js fetch client for single JSON-RPC calls and batches. |

Applications still own deployment-specific choices around the generated client. Server functions, authentication forwarding, UI-framework hooks, and app-specific result normalization can wrap `createRpcApi(...)` rather than replacing the transport implementation.

```ts
import { createRpcApi } from './generated/rpc-client'

const rpc = createRpcApi({
  url: '/rpc',
  headers: { Authorization: `Bearer ${token}` },
})

const abort = new AbortController()
const client = await rpc.clients.get({ id }, { signal: abort.signal })
```

Batch requests are built through generated `$request` helpers and preserve input order:

```ts
const [clientResult, projectsResult] = await rpc.$batch([
  rpc.$request.clients.get({ id }),
  rpc.$request.projects.list({ clientId: id }),
] as const)
```

For SSR or edge/server deployments, pass an injected `fetch` and dynamic headers. Applications that proxy RPC calls through server functions can use this to forward request-scoped authentication headers without forking the generated client:

```ts
const rpc = createRpcApi({
  url: process.env.API_INTERNAL_URL + '/rpc',
  fetch,
  headers: () => ({ Authorization: `Bearer ${forwardedJwt}` }),
  transformResult: normalizeRpcResultForSchema,
})
```

This separation keeps the reusable framework contract small:

- `Elarion.JsonRpc` owns runtime dispatch, telemetry, and schema export.
- `Elarion.AspNetCore` owns HTTP endpoint mapping and ASP.NET Core transport behavior.
- `Elarion.AspNetCore.SchemaGeneration` owns build-time schema export.
- `elarion-jsonrpc-client-generator` owns schema-to-TypeScript/Zod/client generation.
- Applications own server-function/auth/cache adapters around the generated client.

## JSON serialization

Each module should provide a source-generated JSON context for its request/response and nested DTO types:

```csharp
using System.Text.Json.Serialization;

namespace MyApp.Application.Modules.Clients;

[JsonSerializable(typeof(GetClient.Query))]
[JsonSerializable(typeof(GetClient.Response))]
[JsonSerializable(typeof(CreateClient.Command))]
[JsonSerializable(typeof(CreateClient.Response))]
public sealed partial class ClientsJsonContext : JsonSerializerContext;
```

The host combines JSON-RPC envelope metadata, host metadata, module metadata, and a fallback resolver:

```csharp
var moduleResolvers = ModuleBootstrapper.GetAllJsonTypeInfoResolvers(configuration);

var resolvers = new IJsonTypeInfoResolver[2 + moduleResolvers.Length + 1];
resolvers[0] = JsonRpcJsonContext.Default;
resolvers[1] = HostJsonContext.Default;
Array.Copy(moduleResolvers, 0, resolvers, 2, moduleResolvers.Length);
resolvers[^1] = new DefaultJsonTypeInfoResolver();

var options = new JsonSerializerOptions {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers),
};
```

For AOT-sensitive apps, register every command, query, response, and nested DTO type explicitly. Avoid relying on the fallback resolver for production-only types.

## JSON-RPC usage

Mark a handler with `[RpcMethod]`:

```csharp
[RpcMethod("clients.create")]
public sealed class CreateClient
    : IHandler<CreateClient.Command, Result<CreateClient.Response>> {
    public sealed record Command(string Name);
    public sealed record Response(Guid Id);

    public ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        // ...
    }
}
```

The RPC generator expects:

- a nested request type named `Command` or `Query`
- a nested response type named `Response`
- an `IHandler<TRequest, Result<TResponse>>` implementation

The generated map emits typed calls:

```csharp
dispatcher.MapHandler<CreateClient.Command, CreateClient.Response>("clients.create");
```

Each host provides the bridge from Elarion application results to JSON-RPC errors. Different applications can map domain failures to transport errors in their own way.

## Entity Framework Core source generation

The EF Core package is optional. Use it when you want the same compile-time, explicit-convention style for persistence wiring:

```xml
<ItemGroup>
  <ProjectReference Include="..\Elarion.EntityFrameworkCore\Elarion.EntityFrameworkCore.csproj" />
  <ProjectReference Include="..\Elarion.EntityFrameworkCore.Generators\Elarion.EntityFrameworkCore.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Mark entities explicitly:

```csharp
using Elarion.EntityFrameworkCore;

namespace MyApp.Domain.Entities;

[DbEntity]
public sealed class Invoice {
    public Guid Id { get; set; }
}
```

Annotate the application-level DbContext abstraction. This is the source of truth for generated DbSets:

```csharp
using Microsoft.EntityFrameworkCore;
using Elarion.EntityFrameworkCore;

namespace MyApp.Application;

[GenerateDbSets]
public partial interface IAppDbContext {
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    DbContext AsDbContext();
}
```

The generator emits `DbSet<T>` members into that interface and also emits matching concrete members for partial `DbContext` classes that implement it:

```csharp
using Microsoft.EntityFrameworkCore;
using MyApp.Application;

namespace MyApp.Infrastructure.Data;

public sealed partial class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IAppDbContext {
    public DbContext AsDbContext() => this;

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        ConfigureEntities(modelBuilder);
    }
}
```

Do not add `[GenerateDbSets]` to the concrete context. Class generation is intentionally inferred from the generated interface so applications have one declaration point.

For multiple contexts, use string-constant scopes:

```csharp
public static class PersistenceScopes {
    public const string Main = "main";
    public const string AiAgent = "ai-agent";
}

[DbEntity(PersistenceScopes.Main)]
public sealed class Invoice;

[DbEntity(PersistenceScopes.AiAgent)]
public sealed class ChatSession;

[DbEntity(PersistenceScopes.Main, PersistenceScopes.AiAgent)]
public sealed class User;

[GenerateDbSets(PersistenceScopes.Main)]
public partial interface IMainDbContext;

[GenerateDbSets(PersistenceScopes.AiAgent)]
public partial interface IAiAgentDbContext;
```

Scope behavior:

- `[GenerateDbSets]` without scopes keeps global behavior and includes every `[DbEntity]`.
- `[GenerateDbSets("scope")]` includes only entities whose `[DbEntity(...)]` scopes intersect with the interface scopes.
- `[DbEntity]` without scopes participates only in unscoped/global generated interfaces.
- Shared entities can list multiple scopes.

The generator discovers `IEntityTypeConfiguration<T>` implementations in the current and referenced assemblies and emits direct calls equivalent to:

```csharp
new InvoiceConfiguration().Configure(modelBuilder.Entity<Invoice>());
```

Unscoped interfaces preserve legacy behavior and apply every discovered configuration, including schema-only configuration types that are not exposed as `DbSet<T>` properties. Scoped interfaces filter configurations to the selected `[DbEntity]` set so unrelated scoped contexts do not configure each other's entities.

This intentionally avoids `ApplyConfigurationsFromAssembly(...)` reflection scanning and keeps the generated model wiring inspectable and AOT-friendly. The tradeoff is that persistence participation is explicit: entities must opt in with `[DbEntity]`, and scoped contexts require entities to opt into matching scopes.

## Endpoint modules

Modules can declare Minimal API endpoints directly:

```csharp
[AppModule("Chat")]
public static partial class ChatModule {
    public static void MapEndpoints(IEndpointRouteBuilder endpoints) {
        endpoints
            .MapPost("/chat/stream", ChatEndpoint.HandleAsync)
            .RequireAuthorization();
    }
}
```

Use real ASP.NET Core abstractions here. Do not introduce a reduced endpoint facade unless you are willing to give up Minimal API conventions, endpoint filters, typed results, route handler source generation, and ecosystem extension methods.

Keep host lifecycle concerns out of modules. Modules should not call `builder.Build()`, `app.UseAuthentication()`, `app.UseAuthorization()`, `app.MapJsonRpc()`, or configure concrete infrastructure providers.

Concrete infrastructure is treated as platform capability, not as a second module system. Feature flags decide which module handlers, endpoints, JSON metadata, and scheduled jobs are exposed. The platform can still register all default capability providers up front; unused providers stay dormant until a feature resolves the corresponding application port.

## Dependency rules

Use these rules when deciding where code belongs:

| Code | Location |
| --- | --- |
| Generic handler/result/module/pipeline/RPC primitives | `Elarion` |
| Generic Elarion source generation | `Elarion.Generators` |
| JSON-RPC core library | `Elarion.JsonRpc` |
| JSON-RPC / ASP.NET integration library | `Elarion.AspNetCore` |
| Generic EF Core DbSet/configuration source generation | `Elarion.EntityFrameworkCore` and `.Generators` |
| Feature module composition and business handlers | Application project |
| Concrete database/blob/mail/PDF/external service implementation | Infrastructure implementation, registered by the API host as platform capability |
| Middleware, auth, telemetry exporter, app lifetime | API host |
| Application-specific domain code | Consuming application projects |

Application modules may depend on abstraction packages such as DI, configuration, `IEndpointRouteBuilder`, and `System.Text.Json` metadata. They should not depend on the API host, `WebApplicationBuilder`, concrete infrastructure classes, or deployment-specific packages.

## Creating a new module

1. Create a module namespace, for example `MyApp.Application.Modules.Billing`.
2. Add `[AppModule("Billing")]` to a static partial `BillingModule` class.
3. Add `ConfigureServices` and call generated `services.AddBillingHandlers()`, `services.AddBillingServices()`, and `services.AddBillingValidators()`.
4. Add a source-generated `BillingJsonContext`.
5. Add service classes under the module namespace and annotate them with `[Service]`.
6. Add handlers under the module namespace, implementing `IHandler<TRequest, Result<TResponse>>`.
7. Add validators under the same module namespace.
8. Add `[RpcMethod("billing.someAction")]` to handlers that should be exposed through JSON-RPC.
9. Build the host project so Elarion generators emit the registration code.

## Package maintenance checklist

Before adding new public framework surface:

- Keep application-specific generators, handlers, modules, and infrastructure out of the Elarion packages.
- Add or update Roslyn generator tests for source-generation behavior.
- Add or update schema generation tests when changing JSON-RPC exporter behavior, build targets, or host-launching code.
- Extend the generated JSON-RPC client deliberately when repeated frontend runtime adapter patterns emerge.

## Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| `Add{Module}Handlers` is missing | `[assembly: UseElarion]` / `[assembly: GenerateModuleHandlers]` is absent or generator is not referenced as an analyzer. | Add the full or narrow trigger attribute and analyzer project/package reference. |
| `Add{Module}Services` is missing | `[assembly: UseElarion]` / `[assembly: GenerateModuleServices]` is absent, no `[Service]` classes were found in the module namespace, or generator analyzer reference is missing. | Add the full or narrow assembly trigger, annotate service classes with `[Service]`, and verify analyzer reference. |
| A handler is not registered | The class does not implement `Elarion.Abstractions.IHandler<TRequest,TResponse>` or is outside the module namespace. | Check the interface namespace and module namespace containment. |
| A service is not registered | `[Service]` class is outside the module namespace, explicit service contract is invalid, or no contract could be resolved. | Move the class under the module namespace and verify explicit/implicit contract resolution. |
| Hosted service registration fails generator diagnostics | Hosted service uses `Scoped`/`Transient` scope. | Use `Scope = ServiceScope.Singleton` for hosted services. |
| Generic service registration fails generator diagnostics | `[Service]` is placed on a generic type or a type nested in a generic type. | Register open generics manually until the framework defines generated open-generic aliasing semantics. |
| Validators are not registered | Validator is outside the module namespace or does not inherit `AbstractValidator<T>`. | Move it under the module namespace and ensure FluentValidation is referenced. |
| RPC method is missing | Host lacks `[GenerateRpcMethodMap]`, handler lacks `[RpcMethod]`, or nested `Command`/`Query` and `Response` types are missing. | Add the marker and follow the handler shape convention. |
| Build-time schema generation does not run | `ElarionJsonRpcGenerateSchema` is not true, the schema generation package is not referenced, or `ElarionJsonRpcGenerateSchemaOnBuild` was disabled. | Add the private package reference and set `ElarionJsonRpcGenerateSchema=true`, or invoke `GenerateElarionJsonRpcSchema` manually. |
| Build-time schema generation fails after loading the app | Startup code before `builder.Build()` threw, or no frozen `JsonRpcDispatcher` was registered before build. | Register the dispatcher before `builder.Build()` and guard expensive startup work with `JsonRpcSchemaGeneration.IsRunning`. |
| Generated frontend RPC types are stale | `rpc-schema.json` was updated but the client generator was not rerun. | Export the schema and run `npm run generate:rpc` in the frontend app. |
| Client generator fails on schema composition | The schema uses unsupported JSON Schema constructs such as `oneOf`, `anyOf`, or `allOf`. | Extend the generator deliberately or adjust the exported DTO shape. |
| Module services do not run | Module disabled by `Modules:{Name}:Enabled=false` or missing `[AppModule]`. | Check configuration and module attribute. |
| Transaction decorator cannot resolve `DbContext` | Host registered only the concrete context. | Register `DbContext` to resolve to the app context, for example `services.AddScoped<DbContext>(sp => sp.GetRequiredService<AppDbContext>())`. |
| STJ cannot serialize a DTO in AOT mode | Type missing from a module JSON context. | Add `[JsonSerializable(typeof(...))]` to the relevant module context. |
