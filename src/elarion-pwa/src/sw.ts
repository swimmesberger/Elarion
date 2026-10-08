// The service-worker half of the Elarion installable-app shell (ADR-0083): a shell router that makes the Home
// Screen install an app — it opens offline, loads its hashed assets from the cache — without ever answering the API,
// a personal page, or a login redirect from a cache. Import it into the application's service worker (bundled into
// one classic script, or registered with `type: 'module'`) and call registerShellRouter(self, …) once at the top
// level. It composes with `registerWebPushHandlers(self)` from `@swimmesberger/elarion-webpush/sw` in the same worker:
// this module handles `install`, `activate` and `fetch`; Web Push handles `push`, `notificationclick` and
// `pushsubscriptionchange`.
//
//   Navigations                 the app's choice (`navigation.mode`):
//     network-only              never cached — for server-rendered pages that carry personal data; offline, the
//                               precached `offlineUrl` answers instead.
//     network-first             an SPA shell: every route serves the same index.html, kept under `shellUrl` and
//                               served only when the network is gone, so a deploy reaches an installed app on its
//                               next open.
//   `assets.prefix` (/assets/)  cache first — content-hashed, so immutable by construction; `assets.mutable` names
//                               the hash-free files there (network first). The newest `maxEntries` are kept.
//   `staticFiles`               stale-while-revalidate (manifest, icons, favicon); precached.
//   Everything else             not touched: `serverPrefixes` (segment-aware) and `bypass`, any non-GET, cross-origin,
//                               `cache: 'no-store'` or ranged request, and any path not listed above.
//
// Only a plain answer is ever stored: a 200 that did not arrive through a redirect (an auth proxy answering an
// expired session with its login page), is same-origin `basic` (not opaque), and does not say `no-store`. An HTML
// answer is stored only as the navigation shell — never under an asset URL, where a server's SPA fallback answering
// a missing chunk with index.html would otherwise replace the script for good.

/** The paths Elarion's own endpoints map to by default. Spread it into `serverPrefixes` and add the app's own. */
export const ELARION_SERVER_PREFIXES: readonly string[] = [
  '/rpc', // MapElarionJsonRpc
  '/mcp', // MapElarionMcp
  '/events', // MapElarionClientEvents (SSE)
  '/session', // MapElarionSession
  '/webpush', // MapElarionWebPush
  '/_elarion', // blob uploads, downloads and tus
]

/**
 * The parts of `ServiceWorkerGlobalScope` the router uses — structural, so the real `self` fits without this package
 * depending on the WebWorker type library.
 */
export interface ShellServiceWorkerScope {
  readonly location: { readonly origin: string }
  readonly caches: CacheStorage
  fetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response>
  skipWaiting(): Promise<void>
  readonly clients: { claim(): Promise<void> }
  addEventListener(type: string, listener: (event: any) => void): void
}

/** The parts of `FetchEvent` the router uses. */
export interface ShellFetchEvent {
  readonly request: Request
  respondWith(response: Response | Promise<Response>): void
  waitUntil(promise: Promise<unknown>): void
}

interface ExtendableEventLike {
  waitUntil(promise: Promise<unknown>): void
}

/** Answers one fetch event. */
export type ShellStrategy = (event: ShellFetchEvent) => Promise<Response>

/** How page navigations are answered — the application's privacy choice, so it has no default. */
export type NavigationPolicy =
  | {
      /** Never cached. For server-rendered pages: their HTML carries the signed-in user's data. */
      mode: 'network-only'
      /** A self-contained page (no stylesheet or script requests) precached and shown when the network is gone. */
      offlineUrl?: string
    }
  | {
      /** An SPA shell: the last network answer is kept and served only when the network is gone. */
      mode: 'network-first'
      /** The key the shell is cached under — every client route serves the same document. Defaults to `/`. */
      shellUrl?: string
      /** Shown offline before a shell was ever cached. */
      offlineUrl?: string
    }

/** Options for {@link createShellRouter} and {@link registerShellRouter}. */
export interface ShellRouterOptions {
  /** The Cache Storage name. Change it when the precache list changes; `activate` deletes the previous one. */
  cacheName: string
  /**
   * Server endpoints the worker must never answer: the API, auth, uploads, SSE, server-rendered non-app pages.
   * Matched by path segment (`/rpc` covers `/rpc` and `/rpc/x`, not `/rpcx`). Start from
   * {@link ELARION_SERVER_PREFIXES}. Required: in `network-first` mode a navigation outside these is cached as the
   * shell.
   */
  serverPrefixes: readonly string[]
  /** Further requests to leave to the network, e.g. a whole other app under the same origin. */
  bypass?: (url: URL, request: Request) => boolean
  /** How page navigations are answered. */
  navigation: NavigationPolicy
  /** Same-origin files served stale-while-revalidate and precached — manifest, icons, favicon. Not HTML. */
  staticFiles?: readonly string[]
  /** Further URLs to precache on install, beyond `offlineUrl`, `shellUrl` and `staticFiles`. */
  precache?: readonly string[]
  /** The bundler's hashed output, or `false` to leave it alone. */
  assets?:
    | {
        /** Defaults to `/assets/` (Vite). */
        prefix?: string
        /**
         * How many entries under `prefix` are kept, newest first. Hold at least one whole build — a larger app emits
         * hundreds of route chunks, and evicting the running build's own chunks breaks its next lazy route offline.
         * Defaults to 400.
         */
        maxEntries?: number
        /**
         * Files under `prefix` whose URL survives deploys (no content hash) — served network first instead of cache
         * first, which would pin an installed app to the version of its first visit. Defaults to none.
         */
        mutable?: (url: URL) => boolean
      }
    | false
  /** Which other caches `activate` deletes. Defaults to every cache but `cacheName`. */
  ownsCache?: (name: string) => boolean
  /** Activate a new worker without waiting for every tab of the old one to close. Defaults to `true`. */
  skipWaiting?: boolean
}

/** The strategies a router uses, bound to its cache — for an application's own routes in the same `fetch` listener. */
export interface ShellStrategies {
  /** The cache if present, else the network (and store a plain answer). */
  cacheFirst: ShellStrategy
  /** The network (and store a plain answer), else the cache. */
  networkFirst: ShellStrategy
  /** The cache at once, refreshed from the network in the background; the network when nothing is cached yet. */
  staleWhileRevalidate: ShellStrategy
  /** The configured {@link NavigationPolicy}. */
  navigation: ShellStrategy
}

/** A configured shell router; see {@link createShellRouter}. */
export interface ShellRouter {
  /** The strategy that answers `request`, or `null` when the worker must stay out (the browser fetches as usual). */
  route(request: Request): ShellStrategy | null
  /** Answers `event` when {@link route} has a strategy for it; returns whether it did. */
  handleFetch(event: ShellFetchEvent): boolean
  /** Precaches the offline page, the shell and the static files; rejects (failing the install) on a non-plain answer. */
  install(): Promise<void>
  /** Deletes the other caches it owns and claims the open pages. */
  activate(): Promise<void>
  readonly strategies: ShellStrategies
}

/**
 * Whether `pathname` is one of `prefixes` or below one, by path segment: `/rpc` matches `/rpc` and `/rpc/batch` but
 * not `/rpcx`. A trailing slash on a prefix is ignored.
 */
export function isServerPath(pathname: string, prefixes: readonly string[]): boolean {
  return prefixes.some((raw) => {
    const prefix = raw.length > 1 && raw.endsWith('/') ? raw.slice(0, -1) : raw
    return pathname === prefix || pathname.startsWith(prefix === '/' ? '/' : `${prefix}/`)
  })
}

/**
 * Whether a network answer may be stored: a 200 (not partial), not reached through a redirect, same-origin `basic`
 * (an opaque answer's status is unknowable), and not marked `Cache-Control: no-store`.
 */
export function isCacheable(response: Response): boolean {
  return (
    response.status === 200 &&
    !response.redirected &&
    response.type === 'basic' &&
    !/\bno-store\b/i.test(response.headers.get('cache-control') ?? '')
  )
}

function isHtml(response: Response): boolean {
  return /\btext\/html\b/i.test(response.headers.get('content-type') ?? '')
}

/** Creates a shell router without registering it — for a worker that routes some requests itself first. */
export function createShellRouter(scope: ShellServiceWorkerScope, options: ShellRouterOptions): ShellRouter {
  const { cacheName, serverPrefixes, navigation: policy } = options
  const staticFiles = new Set(options.staticFiles ?? [])
  const assets = options.assets === false ? null : (options.assets ?? {})
  const assetPrefix = assets?.prefix ?? '/assets/'
  const maxAssets = assets?.maxEntries ?? 400
  const shellUrl = policy.mode === 'network-first' ? (policy.shellUrl ?? '/') : null
  const offlineUrl = policy.offlineUrl ?? null
  const precache = [
    ...new Set([
      ...(offlineUrl ? [offlineUrl] : []),
      ...(shellUrl ? [shellUrl] : []),
      ...staticFiles,
      ...(options.precache ?? []),
    ]),
  ]

  const open = () => scope.caches.open(cacheName)

  /** Stores without failing the response: a full quota or a `Vary: *` answer only means it is not cached. */
  async function store(key: RequestInfo, response: Response): Promise<void> {
    try {
      await (await open()).put(key, response)
    } catch {
      // Not cached this time.
    }
  }

  /** Caches a copy of a plain, non-HTML answer in the background of `event`. */
  function keep(event: ShellFetchEvent, response: Response, after?: () => Promise<void>): void {
    if (!isCacheable(response) || isHtml(response)) return
    event.waitUntil(store(event.request, response.clone()).then(after))
  }

  async function trimAssets(): Promise<void> {
    const cache = await open()
    const keys = (await cache.keys()).filter((key) => new URL(key.url).pathname.startsWith(assetPrefix))
    // Cache keys come back in insertion order, so the oldest entries (earlier deploys) go first.
    for (const key of keys.slice(0, Math.max(0, keys.length - maxAssets))) await cache.delete(key)
  }

  function cacheFirstWith(after?: () => Promise<void>): ShellStrategy {
    return async (event) => {
      const cached = await (await open()).match(event.request)
      if (cached) return cached
      const response = await scope.fetch(event.request)
      keep(event, response, after)
      return response
    }
  }

  const cacheFirst = cacheFirstWith()
  const assetCacheFirst = cacheFirstWith(trimAssets)

  const networkFirst: ShellStrategy = async (event) => {
    try {
      const response = await scope.fetch(event.request)
      keep(event, response)
      return response
    } catch {
      return (await (await open()).match(event.request)) ?? Response.error()
    }
  }

  const staleWhileRevalidate: ShellStrategy = async (event) => {
    const cached = await (await open()).match(event.request)
    const refresh = scope.fetch(event.request).then(
      (response) => {
        keep(event, response)
        return response
      },
      () => undefined,
    )
    if (cached) {
      event.waitUntil(refresh)
      return cached
    }
    return (await refresh) ?? Response.error()
  }

  async function offline(): Promise<Response> {
    const cache = await open()
    const shell = shellUrl ? await cache.match(shellUrl) : undefined
    const page = shell ?? (offlineUrl ? await cache.match(offlineUrl) : undefined)
    return page ?? Response.error()
  }

  const navigation: ShellStrategy = async (event) => {
    let response: Response
    try {
      response = await scope.fetch(event.request)
    } catch {
      return offline()
    }
    // network-only stores nothing; network-first keeps a plain HTML answer as the shell.
    if (shellUrl && isCacheable(response) && isHtml(response)) {
      event.waitUntil(store(shellUrl, response.clone()))
    }
    return response
  }

  function route(request: Request): ShellStrategy | null {
    if (request.method !== 'GET') return null
    if (request.cache === 'no-store') return null
    if (request.headers?.has('range')) return null
    const url = new URL(request.url)
    if (url.origin !== scope.location.origin) return null
    if (isServerPath(url.pathname, serverPrefixes)) return null
    if (options.bypass?.(url, request)) return null

    if (request.mode === 'navigate') return navigation
    if (assets && url.pathname.startsWith(assetPrefix)) {
      return assets.mutable?.(url) ? networkFirst : assetCacheFirst
    }
    if (staticFiles.has(url.pathname)) return staleWhileRevalidate
    return null
  }

  return {
    route,
    handleFetch(event) {
      const strategy = route(event.request)
      if (!strategy) return false
      event.respondWith(strategy(event))
      return true
    },
    async install() {
      const cache = await open()
      await Promise.all(
        precache.map(async (url) => {
          // `reload` skips the HTTP cache: a stale copy would outlive the deploy it belonged to.
          const response = await scope.fetch(url, { cache: 'reload', credentials: 'same-origin' })
          if (!isCacheable(response)) {
            throw new Error(
              `Precaching ${url} failed: HTTP ${response.status}${response.redirected ? ', redirected' : ''}.`,
            )
          }
          // Only the offline page and the shell are documents; HTML anywhere else is a server's SPA fallback.
          if (url !== offlineUrl && url !== shellUrl && isHtml(response)) {
            throw new Error(`Precaching ${url} failed: the server answered with an HTML page.`)
          }
          await cache.put(url, response)
        }),
      )
    },
    async activate() {
      const ownsCache = options.ownsCache ?? (() => true)
      const names = await scope.caches.keys()
      await Promise.all(
        names.filter((name) => name !== cacheName && ownsCache(name)).map((name) => scope.caches.delete(name)),
      )
      await scope.clients.claim()
    },
    strategies: { cacheFirst, networkFirst, staleWhileRevalidate, navigation },
  }
}

/**
 * Registers the shell router's `install`, `activate` and `fetch` handlers on `scope` and returns the router.
 *
 * @example
 * ```ts
 * import { ELARION_SERVER_PREFIXES, registerShellRouter } from '@swimmesberger/elarion-pwa/sw'
 * import { registerWebPushHandlers } from '@swimmesberger/elarion-webpush/sw'
 *
 * registerShellRouter(self, {
 *   cacheName: 'myapp-v1',
 *   serverPrefixes: [...ELARION_SERVER_PREFIXES, '/auth'],
 *   navigation: { mode: 'network-only', offlineUrl: '/offline.html' },
 *   staticFiles: ['/manifest.webmanifest', '/icon-192.png', '/icon-512.png', '/apple-touch-icon.png'],
 * })
 * registerWebPushHandlers(self, { icon: '/icon-192.png' })
 * ```
 */
export function registerShellRouter(scope: ShellServiceWorkerScope, options: ShellRouterOptions): ShellRouter {
  const router = createShellRouter(scope, options)
  scope.addEventListener('install', (event: ExtendableEventLike) => {
    event.waitUntil(router.install())
    if (options.skipWaiting !== false) void scope.skipWaiting().catch(() => undefined)
  })
  scope.addEventListener('activate', (event: ExtendableEventLike) => {
    event.waitUntil(router.activate())
  })
  scope.addEventListener('fetch', (event: ShellFetchEvent) => {
    router.handleFetch(event)
  })
  return router
}
