# Changelog

All notable changes to Elarion are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While Elarion is pre-1.0,
minor releases may include breaking changes.

## [Unreleased]

### Added
- Restructured documentation into a navigable, multi-page guide under [`docs/`](docs/), covering
  getting started, core concepts, JSON-RPC, scheduling, resilience, EF Core, telemetry, and
  reference material.
- Dedicated documentation for handler caching (`[Cacheable]`, `[CacheInvalidate]`) and current-user
  access (`ICurrentUser`).
- Project health files: `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, this changelog, and
  GitHub issue/PR templates.
- Project logo and icon.

## [0.1.0] - Unreleased

Initial preview line.

### Added
- Module-based application framework with `[AppModule]`, compile-time handler/service/validator
  registration, and a module bootstrapper generator.
- `IHandler<TRequest, TResponse>` use-case model with `Result<T>` and transport-agnostic `AppError`.
- Generated decorator pipelines with assembly/module/handler scoping.
- Declarative handler caching backed by `HybridCache`.
- Transport-neutral JSON-RPC: dispatcher, ASP.NET Core endpoint mapping, batch execution, build-time
  schema export, and a TypeScript/Zod client generator.
- In-memory scheduler with source-generated jobs, fixed-rate/fixed-delay/cron schedules, overlap and
  misfire policies, and runtime one-off jobs.
- Resilience policies (`[ResiliencePolicy]`, `[Resilient]`) with handler and scheduler integration,
  backed by a pluggable runtime (default: Microsoft.Extensions.Resilience / Polly).
- Optional Entity Framework Core source generation for `DbSet`s and entity configuration.
- OpenTelemetry-compatible tracing and metrics for JSON-RPC, scheduling, caching, and resilience.

[Unreleased]: https://github.com/swimmesberger/Elarion/compare/main...HEAD
[0.1.0]: https://github.com/swimmesberger/Elarion/releases/tag/v0.1.0
