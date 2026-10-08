# ADR-0083: The installable-app shell is a framework npm package — safe caching rules built in, the privacy choice left to the app

- Status: Proposed
- Date: 2026-10-08
- Related: [ADR-0076](0076-web-push.md) (Web Push, whose service-worker module this composes with),
  [ADR-0032](0032-frontend-contribution-model.md) (applications own their payloads and shell composition),
  [ADR-0017](0017-dependency-light-core.md) (opt-in siblings, no heavy defaults), and the
  [Installable apps](../capabilities/pwa.mdx) capability page.

## Context

Three applications on Elarion made their frontends installable on a phone, each with a hand-written classic
`public/sw.js` of 130–150 lines and page-side helpers. Their stale-while-revalidate, cache-first, `activate`
cleanup and fetch preamble (GET only, same origin, a bypass list) were byte-identical apart from semicolons. iOS and
standalone detection existed four times, once privately inside `@swimmesberger/elarion-webpush`. Reloading an
installed app into a new deploy was solved twice, differently: a JSON version file checked on foreground return with
a loop guard and a "busy" deferral, and an entry-script comparison that reloaded immediately. The third app had none,
so a new build reached it only on a cold start.

The copies also differed in their safety, and each difference was a bug in one of them:

- a plain `startsWith('/rpc')` bypass also matches `/rpcx`, and `'/api/'` misses `/api`;
- one worker did not skip `cache: 'no-store'`, so the app's own update check could be answered from a cache;
- two stored any `response.ok` answer, so an auth proxy's login page — reached through a redirect, a 200 once
  followed — could be stored under an asset URL and served as the script for good by cache-first;
- an asset cap of 150 evicted the running build's own route chunks (one build emitted about 310 files);
- a hash-free stylesheet served cache-first pinned an installed app to the stylesheet of its first visit;
- reloading on foreground return wiped a half-filled form after the user came back from the camera.

## Decision

Ship the shell as a new npm package, `@swimmesberger/elarion-pwa`, with three entries:

- **Main** (framework-neutral, page side): `createInstallPrompt()` (captures `beforeinstallprompt`, reports
  `'prompt' | 'ios' | 'none'`), `createUpdateWatcher()`, `registerServiceWorker()` (`updateViaCache: 'none'`), and the
  pure decision functions behind them. Controllers expose a subscribe/snapshot pair, so React binds them with
  `useSyncExternalStore` and no `/react` entry is needed.
- **`/sw`**: `createShellRouter`/`registerShellRouter(self, options)` handling `install`, `activate` and `fetch`,
  plus its strategies bound to its cache for an application's own routes. It handles no push events, so
  `registerWebPushHandlers(self)` composes in the same worker.
- **`/vite`**: `appVersionFile()`, a structurally typed plugin (Vite is an optional peer, never imported) that emits
  `/app-version.json` in the client environment and optionally `define`s the same version into the bundle.

### The safety rules are not options

The router never answers a non-GET, cross-origin, `cache: 'no-store'`, or ranged request, or a path under
`serverPrefixes` (matched by path segment) — and it answers only what it was told about (navigations, the asset
prefix, listed static files). It stores only a plain answer: status 200, not redirected, same-origin `basic`, not
`Cache-Control: no-store`; and HTML only as the `network-first` shell, never under an asset URL. A precache answer
that is not plain fails the install rather than storing a login page as the offline page. None of these can be
switched off: every one is a bug a source application shipped, and an application that needs different behavior
routes that request itself before handing the event to the router.

### The privacy choice stays with the application

`navigation` has no default. `network-only` (never cache a page; an offline page instead) is right for
server-rendered HTML that carries the signed-in user's data; `network-first` (keep the last shell) is right for a
static SPA shell. Choosing silently for the application would either leak personal pages into Cache Storage or take
the offline shell away from the SPAs. `serverPrefixes` is required for the same reason — in `network-first` mode any
navigation outside it is stored as the shell — and `ELARION_SERVER_PREFIXES` lists only the framework's own default
routes for the application to extend. The cache name, the precache list, the manifest, the icons, the offline page
and every UI string remain application-owned.

### Reload on a safe navigation, never on resume

The update watcher checks on foreground return (at most once per minute) and only marks a different served version
as pending. It reloads on the next resolved navigation whose **path** changed, while no dialog is open and no text
field is focused, and at most once per minute per tab (a `sessionStorage` guard that survives the reload, against a
proxy serving a stale version file). A foreground return cannot be told apart from returning from the camera or a
share sheet, and iOS offers no `beforeunload` prompt, so no reload on resume is ever safe; a path change means the
user has left the page, and pages with unsaved input already guard their own navigation. The router seam is a single
call — `navigationResolved({ pathChanged })` or `navigatedTo(pathname)` — so any router adapts in one line and the
package imports none.

### A new package rather than an entry in `elarion-webpush`

Installing, updating and offline startup are useful without push, and an app that never notifies should not take a
push package to get them. The cost of a separate package is mechanical: a matrix entry in `ci.yml` and `publish.yml`
and one npm trusted-publisher registration; versioning follows `VersionPrefix` like every other package. The shared
iOS/standalone detection stays duplicated in `elarion-webpush` for now rather than making the push package depend on
this one.

## Consequences

- The three applications delete their hand-written workers' routing and their install/update helpers and keep a
  configuration block; the bypass, redirect, `no-store`, HTML-as-asset and asset-cap bugs are fixed once.
- A worker that imports the package must be bundled (one classic script via a second `vite build`, or a module
  worker). All three applications ship an unbundled `public/sw.js` today; the capability page documents the
  second-build recipe one of them already uses on a branch to import the Web Push handlers.
- Changing a safety rule is a framework change with a regression test (`tests/sw.test.ts` drives the router with
  real `Request`/`Response` objects against a fake Cache Storage), not a per-app edit.
- Precaching is a fixed list, not a build manifest: assets are cached on first use. Full offline-first from the first
  visit remains a job for Workbox; this package does not grow toward it.
- Caching headers stay a host recipe (ASP.NET Core `StaticFileOptions`, Caddy) on the capability page rather than an
  `Elarion.AspNetCore` API: deployment conventions belong to the application, and the hosts differ.
