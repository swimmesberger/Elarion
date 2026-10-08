import { describe, expect, it } from 'vitest'
import { registerWebPushHandlers, type WebPushServiceWorkerScope } from '../../elarion-webpush/src/sw.js'
import {
  ELARION_SERVER_PREFIXES,
  isCacheable,
  isServerPath,
  registerShellRouter,
  type ShellRouterOptions,
  type ShellServiceWorkerScope,
} from '../src/sw.js'

// The routing rules are the privacy line of an installed app: server-rendered pages can carry personal data, API
// answers are per session, and an auth proxy's login page must never become a cached script. So the router runs
// against a fake worker scope — real Request/Response objects, a Cache Storage fake, a scripted network.

const ORIGIN = 'https://app.example'

interface NetworkAnswer {
  body?: string
  status?: number
  contentType?: string
  cacheControl?: string
  redirected?: boolean
  type?: ResponseType
}

function keyOf(input: RequestInfo | URL): string {
  const href = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
  const url = new URL(href, ORIGIN)
  return url.pathname + url.search
}

function fakeWorker(options: Partial<ShellRouterOptions> = {}) {
  const listeners = new Map<string, (event: any) => void>()
  const caches = new Map<string, Map<string, Response>>()
  const network: { path: string; init?: RequestInit }[] = []
  const state = {
    offline: false,
    answer: (_path: string): NetworkAnswer => ({}),
    claimed: false,
    skippedWaiting: false,
  }

  function cacheFor(name: string): Cache {
    let entries = caches.get(name)
    if (!entries) caches.set(name, (entries = new Map()))
    const store = entries
    return {
      match: async (request: RequestInfo | URL) => store.get(keyOf(request))?.clone(),
      put: async (request: RequestInfo | URL, response: Response) => {
        const key = keyOf(request)
        store.delete(key)
        store.set(key, response)
      },
      keys: async () => [...store.keys()].map((path) => new Request(ORIGIN + path)),
      delete: async (request: RequestInfo | URL) => store.delete(keyOf(request)),
    } as unknown as Cache
  }

  const scope: ShellServiceWorkerScope = {
    location: { origin: ORIGIN },
    caches: {
      open: async (name: string) => cacheFor(name),
      keys: async () => [...caches.keys()],
      delete: async (name: string) => caches.delete(name),
      has: async (name: string) => caches.has(name),
      match: async () => undefined,
    } as unknown as CacheStorage,
    fetch: async (input, init) => {
      const path = keyOf(input as RequestInfo)
      network.push({ path, init })
      if (state.offline) throw new TypeError('Failed to fetch')
      const answer = state.answer(path)
      const headers = new Headers({ 'content-type': answer.contentType ?? 'application/javascript' })
      if (answer.cacheControl) headers.set('cache-control', answer.cacheControl)
      const response = new Response(answer.body ?? `network ${path}`, { status: answer.status ?? 200, headers })
      // What a browser's same-origin fetch reports; a constructed Response says 'default' and not redirected.
      Object.defineProperty(response, 'type', { value: answer.type ?? 'basic' })
      Object.defineProperty(response, 'redirected', { value: answer.redirected ?? false })
      return response
    },
    skipWaiting: async () => {
      state.skippedWaiting = true
    },
    clients: {
      claim: async () => {
        state.claimed = true
      },
    },
    addEventListener: (type, listener) => listeners.set(type, listener),
  }

  const router = registerShellRouter(scope, {
    cacheName: 'app-v2',
    serverPrefixes: [...ELARION_SERVER_PREFIXES, '/auth', '/api/'],
    navigation: { mode: 'network-only', offlineUrl: '/offline.html' },
    staticFiles: ['/manifest.webmanifest', '/icon-192.png'],
    assets: { mutable: (url) => url.pathname.endsWith('.css') },
    ...options,
  })

  async function lifecycle(type: 'install' | 'activate') {
    const pending: Promise<unknown>[] = []
    listeners.get(type)!({ waitUntil: (promise: Promise<unknown>) => pending.push(promise) })
    await Promise.all(pending)
  }

  /** Dispatches a fetch event; resolves to the worker's answer, or `null` when it stayed out. */
  async function dispatch(
    path: string,
    init: { method?: string; mode?: RequestMode; cache?: RequestCache; headers?: HeadersInit; url?: string } = {},
  ): Promise<Response | null> {
    const request = {
      url: init.url ?? ORIGIN + path,
      method: init.method ?? 'GET',
      mode: init.mode ?? 'cors',
      cache: init.cache ?? 'default',
      headers: new Headers(init.headers),
    } as Request
    let responded: Promise<Response> | Response | null = null
    const background: Promise<unknown>[] = []
    listeners.get('fetch')!({
      request,
      respondWith: (response: Promise<Response> | Response) => (responded = response),
      waitUntil: (promise: Promise<unknown>) => background.push(promise),
    })
    const response = responded === null ? null : await responded
    // Background work (cache writes, revalidation) may queue more background work.
    for (let settled = 0; settled < background.length; settled = background.length) await Promise.all(background)
    return response
  }

  const stored = (name = 'app-v2') => [...(caches.get(name)?.keys() ?? [])]
  const fetched = () => network.map((entry) => entry.path)

  return { router, state, caches, network, fetched, stored, dispatch, lifecycle, listeners }
}

describe('what the worker never touches', () => {
  it('leaves every server endpoint to the network, matched by path segment', async () => {
    const worker = fakeWorker()
    for (const path of ['/rpc', '/rpc/batch', '/mcp', '/events', '/session', '/webpush/subscribe', '/_elarion/blobs/tus/1', '/auth/login', '/api', '/api/v1/x']) {
      expect(await worker.dispatch(path), path).toBeNull()
      expect(await worker.dispatch(path, { mode: 'navigate' }), `${path} (navigation)`).toBeNull()
    }
    // A prefix is a path segment, not a string prefix: /rpcx is an app route.
    expect(await worker.dispatch('/rpcx', { mode: 'navigate' })).not.toBeNull()
    expect(worker.stored()).toEqual([])
  })

  it('leaves non-GET, no-store, ranged and cross-origin requests to the network', async () => {
    const worker = fakeWorker()
    expect(await worker.dispatch('/assets/index-B3xK9aZq.js', { method: 'POST' })).toBeNull()
    expect(await worker.dispatch('/properties', { method: 'POST', mode: 'navigate' })).toBeNull()
    // The update check asks with no-store: it must see what is deployed now.
    expect(await worker.dispatch('/app-version.json', { cache: 'no-store' })).toBeNull()
    expect(await worker.dispatch('/assets/index-B3xK9aZq.js', { cache: 'no-store' })).toBeNull()
    expect(await worker.dispatch('/assets/clip-Ab12Cd34.mp4', { headers: { range: 'bytes=0-' } })).toBeNull()
    expect(await worker.dispatch('/assets/x.js', { url: 'https://cdn.example/assets/x.js' })).toBeNull()
    expect(worker.network).toEqual([])
  })

  it('leaves unlisted same-origin files and the bypass predicate to the network', async () => {
    const worker = fakeWorker({ bypass: (url) => url.pathname.startsWith('/legacy/') })
    expect(await worker.dispatch('/robots.txt')).toBeNull()
    expect(await worker.dispatch('/legacy/page', { mode: 'navigate' })).toBeNull()
  })
})

describe('navigations, network-only', () => {
  it('always comes from the network and is never stored', async () => {
    const worker = fakeWorker()
    worker.state.answer = () => ({ contentType: 'text/html', body: '<h1>Jane Doe, 1 Main St</h1>' })

    const response = await worker.dispatch('/tenants/42', { mode: 'navigate' })

    expect(await response!.text()).toBe('<h1>Jane Doe, 1 Main St</h1>')
    expect(worker.stored()).toEqual([])
  })

  it('answers with the precached offline page when the network is gone', async () => {
    const worker = fakeWorker()
    worker.state.answer = (path) => (path === '/offline.html' ? { contentType: 'text/html', body: 'offline page' } : {})
    await worker.lifecycle('install')
    worker.state.offline = true

    const response = await worker.dispatch('/tenants', { mode: 'navigate' })

    expect(await response!.text()).toBe('offline page')
  })
})

describe('navigations, network-first shell', () => {
  const shell: Partial<ShellRouterOptions> = { navigation: { mode: 'network-first', offlineUrl: '/offline.html' } }

  it('keeps the latest HTML answer as the shell and serves it for every route offline', async () => {
    const worker = fakeWorker(shell)
    worker.state.answer = () => ({ contentType: 'text/html; charset=utf-8', body: 'shell build 2' })

    expect(await (await worker.dispatch('/stacks/7', { mode: 'navigate' }))!.text()).toBe('shell build 2')
    worker.state.offline = true

    expect(await (await worker.dispatch('/routes', { mode: 'navigate' }))!.text()).toBe('shell build 2')
    expect(worker.stored()).toEqual(['/'])
  })

  it('never keeps a redirected, non-HTML, failed or no-store answer as the shell', async () => {
    const worker = fakeWorker(shell)
    for (const answer of [
      { contentType: 'text/html', redirected: true, body: 'proxy login page' },
      { contentType: 'application/json' },
      { contentType: 'text/html', status: 500 },
      { contentType: 'text/html', cacheControl: 'private, no-store' },
    ] satisfies NetworkAnswer[]) {
      worker.state.answer = () => answer
      await worker.dispatch('/', { mode: 'navigate' })
    }

    expect(worker.stored()).toEqual([])
  })

  it('falls back to the offline page before any shell was cached, else to a network error', async () => {
    const worker = fakeWorker(shell)
    worker.state.offline = true

    expect((await worker.dispatch('/stacks', { mode: 'navigate' }))!.type).toBe('error')

    worker.caches.set('app-v2', new Map([['/offline.html', new Response('offline page')]]))
    expect(await (await worker.dispatch('/stacks', { mode: 'navigate' }))!.text()).toBe('offline page')
  })
})

describe('assets', () => {
  it('serves content-hashed files cache-first and hash-free ones network-first', async () => {
    const worker = fakeWorker()

    await worker.dispatch('/assets/index-B3xK9aZq.js')
    await worker.dispatch('/assets/index-B3xK9aZq.js')
    expect(worker.fetched()).toEqual(['/assets/index-B3xK9aZq.js'])

    await worker.dispatch('/assets/styles.css')
    await worker.dispatch('/assets/styles.css')
    expect(worker.fetched().slice(1)).toEqual(['/assets/styles.css', '/assets/styles.css'])

    worker.state.offline = true
    expect(await (await worker.dispatch('/assets/styles.css'))!.text()).toBe('network /assets/styles.css')
  })

  it('never stores an answer that is redirected, opaque, partial, no-store or HTML', async () => {
    // An auth proxy redirects an expired session to its login page, and an SPA fallback answers a missing chunk
    // with index.html; stored under an asset URL, cache-first would serve either as the script for good.
    const worker = fakeWorker()
    for (const answer of [
      { redirected: true }, // followed to anything — the redirect alone disqualifies it
      { type: 'opaque' },
      { status: 206 },
      { cacheControl: 'no-store' },
      { contentType: 'text/html', body: '<!doctype html>' },
    ] satisfies NetworkAnswer[]) {
      worker.state.answer = () => answer
      for (const path of ['/assets/index-B3xK9aZq.js', '/assets/styles.css', '/icon-192.png']) {
        await worker.dispatch(path)
        expect(worker.stored(), `${JSON.stringify(answer)} ${path}`).toEqual([])
      }
    }

    worker.state.answer = () => ({})
    await worker.dispatch('/assets/index-B3xK9aZq.js')
    expect(worker.stored()).toEqual(['/assets/index-B3xK9aZq.js'])
  })

  it('keeps the newest maxEntries assets and drops the oldest first', async () => {
    const worker = fakeWorker({ assets: { maxEntries: 3 }, staticFiles: ['/icon-192.png'] })
    await worker.dispatch('/icon-192.png')
    for (const n of [1, 2, 3, 4, 5]) await worker.dispatch(`/assets/chunk-${n}.js`)

    expect(worker.stored()).toEqual(['/icon-192.png', '/assets/chunk-3.js', '/assets/chunk-4.js', '/assets/chunk-5.js'])
  })

  it('leaves the assets alone when assets is false', async () => {
    const worker = fakeWorker({ assets: false })
    expect(await worker.dispatch('/assets/index-B3xK9aZq.js')).toBeNull()
  })
})

describe('static files', () => {
  it('answers from the cache at once and refreshes it in the background', async () => {
    const worker = fakeWorker()
    let build = 1
    worker.state.answer = () => ({ contentType: 'application/manifest+json', body: `manifest ${build}` })

    expect(await (await worker.dispatch('/manifest.webmanifest'))!.text()).toBe('manifest 1')
    build = 2
    expect(await (await worker.dispatch('/manifest.webmanifest'))!.text()).toBe('manifest 1')
    expect(await (await worker.dispatch('/manifest.webmanifest'))!.text()).toBe('manifest 2')
  })
})

describe('lifecycle', () => {
  it('precaches the offline page and static files past the HTTP cache, then skips waiting', async () => {
    const worker = fakeWorker()
    worker.state.answer = (path) => ({ contentType: path.endsWith('.html') ? 'text/html' : 'image/png' })

    await worker.lifecycle('install')

    expect(worker.stored().sort()).toEqual(['/icon-192.png', '/manifest.webmanifest', '/offline.html'])
    expect(worker.network.every((entry) => entry.init?.cache === 'reload')).toBe(true)
    expect(worker.state.skippedWaiting).toBe(true)
  })

  it('fails the install rather than precache a login page', async () => {
    const worker = fakeWorker()
    worker.state.answer = () => ({ contentType: 'text/html', redirected: true, body: 'proxy login page' })

    await expect(worker.lifecycle('install')).rejects.toThrow(/offline\.html.*redirected/)
  })

  it('fails the install rather than precache an SPA fallback page as an icon', async () => {
    const worker = fakeWorker()
    worker.state.answer = () => ({ contentType: 'text/html', body: '<!doctype html>' })

    await expect(worker.lifecycle('install')).rejects.toThrow(/(manifest\.webmanifest|icon-192\.png).*HTML page/)
  })

  it('deletes the caches it owns on activate and claims the open pages', async () => {
    const worker = fakeWorker({ ownsCache: (name) => name.startsWith('app-') })
    for (const name of ['app-v1', 'app-v2', 'other-library']) worker.caches.set(name, new Map())

    await worker.lifecycle('activate')

    expect([...worker.caches.keys()]).toEqual(['app-v2', 'other-library'])
    expect(worker.state.claimed).toBe(true)
  })
})

describe('helpers', () => {
  it('matches server prefixes by segment, ignoring a trailing slash', () => {
    expect(isServerPath('/api', ['/api/'])).toBe(true)
    expect(isServerPath('/api/x', ['/api/'])).toBe(true)
    expect(isServerPath('/apix', ['/api/'])).toBe(false)
    expect(isServerPath('/rpcx', ELARION_SERVER_PREFIXES)).toBe(false)
  })

  it('caches only a plain same-origin 200', () => {
    const plain = new Response('x')
    Object.defineProperty(plain, 'type', { value: 'basic' })
    expect(isCacheable(plain)).toBe(true)
    expect(isCacheable(new Response('x'))).toBe(false) // a constructed Response is 'default', not 'basic'
  })
})

describe('composition with Web Push', () => {
  it('shares one worker with registerWebPushHandlers without either taking the other’s events', async () => {
    const worker = fakeWorker()
    const shown: string[] = []
    const pushScope = {
      addEventListener: (type: string, listener: (event: any) => void) => {
        expect(worker.listeners.has(type), `${type} registered twice`).toBe(false)
        worker.listeners.set(type, listener)
      },
      registration: {
        scope: `${ORIGIN}/`,
        pushManager: {} as PushManager,
        showNotification: async (title: string) => {
          shown.push(title)
        },
      },
      clients: { matchAll: async () => [], openWindow: async () => undefined },
    } satisfies WebPushServiceWorkerScope

    registerWebPushHandlers(pushScope, { api: null })
    const pending: Promise<unknown>[] = []
    worker.listeners.get('push')!({ data: { text: () => '{"title":"Deploy failed"}' }, waitUntil: (p: Promise<unknown>) => pending.push(p) })
    await Promise.all(pending)

    expect(shown).toEqual(['Deploy failed'])
    expect([...worker.listeners.keys()].sort()).toEqual(
      ['activate', 'fetch', 'install', 'notificationclick', 'push', 'pushsubscriptionchange'].sort(),
    )
    expect(await worker.dispatch('/assets/index-B3xK9aZq.js')).not.toBeNull()
  })
})
