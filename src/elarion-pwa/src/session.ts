// Renew an expired proxy session. An authenticating proxy in front of the app (Cloudflare Access, Google IAP,
// oauth2-proxy) issues its session for a fixed time and renews it — silently, from its own longer-lived session and
// the identity provider's — only on a full page load: the redirect chain runs through other origins, which a fetch
// cannot follow. An installed app is resumed rather than reloaded, so it meets the expiry on an API call instead:
//
//   fetch        the proxy redirects to its login page on another origin, and the browser reports a CORS failure,
//                indistinguishable from being offline. Marked as an XHR (`X-Requested-With: XMLHttpRequest`, which
//                Cloudflare Access, Google IAP and ASP.NET Core's cookie handler honour) or as a JSON call
//                (`Accept: application/json`, oauth2-proxy), it answers 401 instead; with `redirect: 'manual'` a
//                proxy that redirects anyway yields a readable opaque redirect instead of the CORS failure.
//   EventSource  cannot send headers. Redirected off-origin it retries forever; answered with a 401 or a login page
//                it gives up. Neither says why.
//
// createSessionRenewal turns both into one signal — the session expired — and answers it with a single reload, which
// lets the proxy renew the session and lands the user where they were. A sessionStorage guard keeps a proxy that
// answers 401 right after the reload from turning it into a loop.

import type { ReloadGuardStorage } from './update.js'

/** The headers that make authenticating proxies answer an expired session with 401 instead of a login redirect. */
export const SESSION_REQUEST_HEADERS: Readonly<Record<string, string>> = {
  'X-Requested-With': 'XMLHttpRequest',
  Accept: 'application/json',
}

/** No second renewal reload within this window: a 401 right after one is not an expiry the reload can renew. */
export const DEFAULT_SESSION_RENEWAL_GUARD_MS = 60_000

/** The sessionStorage key of the last renewal reload (per tab; it survives the reload itself). */
export const DEFAULT_SESSION_RENEWAL_STORAGE_KEY = 'elarion.pwa.sessionRenewedAt'

/** No second probe of an event stream within this window, however often it reports an error. */
export const DEFAULT_SESSION_PROBE_INTERVAL_MS = 10_000

/**
 * Whether an API answer means the session expired: a 401, or a redirect (fetched with `redirect: 'manual'`) — an API
 * endpoint never redirects, a proxy sending the browser to its login page does.
 */
export function isSessionExpired(response: Pick<Response, 'status' | 'type'>): boolean {
  return response.status === 401 || response.type === 'opaqueredirect'
}

/** The parts of the platform `EventSource` the renewal needs — and the shape the generated events client accepts. */
export interface SessionEventSource {
  addEventListener(type: string, listener: (event: any) => void): void
  close(): void
  readonly readyState: number
}

/** Options for {@link createSessionRenewal}. */
export interface SessionRenewalOptions {
  /** Minimum time between two renewal reloads of this tab. Defaults to 60 s. */
  reloadGuardMs?: number
  /** The sessionStorage key of the reload guard. Defaults to {@link DEFAULT_SESSION_RENEWAL_STORAGE_KEY}. */
  storageKey?: string
  /**
   * Where the reload guard is kept. Defaults to `sessionStorage`. `null` disables the automatic reload: without a
   * guard that survives the reload, a proxy that keeps answering 401 would reload the page forever.
   */
  storage?: ReloadGuardStorage | null
  /**
   * Called when the session expired but the page will not reload — a renewal ran moments ago, or there is no guard
   * storage. The proxy needs a real sign-in: show a "signed out" state with a button that reloads.
   */
  onSignedOut?: () => void
  /** Minimum time between two probes of an event stream. Defaults to 10 s. */
  probeIntervalMs?: number
  /** The `fetch` behind {@link SessionRenewal.fetch} and the event-stream probe. Defaults to the global one. */
  fetch?: typeof fetch
  /** Opens an event stream. Defaults to `new EventSource(url)`. */
  createEventSource?: (url: string) => SessionEventSource
  /** Reloads the page. Defaults to `location.reload()`. */
  reload?: () => void
  /** The clock. Defaults to `Date.now`. */
  now?: () => number
}

/** Session renewal for an app behind an authenticating proxy; see {@link createSessionRenewal}. */
export interface SessionRenewal {
  /**
   * A `fetch` for the app's API — the generated JSON-RPC client's `fetch` option. It adds
   * {@link SESSION_REQUEST_HEADERS} (unless the call sets them) and `redirect: 'manual'` (unless the call sets
   * `redirect`); when the answer means the session expired it reloads the page and never settles, so the page goes
   * away instead of flashing an error first. When it does not reload ({@link SessionRenewalOptions.onSignedOut}) the
   * answer passes through.
   */
  fetch(input: string | URL | Request, init?: RequestInit): Promise<Response>
  /**
   * Opens an event stream — the generated events client's `eventSource` option. When the stream reports an error it
   * probes the same URL once with {@link SESSION_REQUEST_HEADERS}, and renews the session when the probe says it
   * expired. Network errors (offline, a server restart) are left to the stream's own retries.
   */
  eventSource(url: string): SessionEventSource
  /**
   * Reloads the page to renew the session, unless a renewal ran within the guard window (then it calls `onSignedOut`).
   * Returns whether it reloaded.
   */
  renew(): boolean
}

/**
 * Creates the session renewal: pass its `fetch` to the JSON-RPC client and its `eventSource` to the client-events
 * client, and an expired proxy session renews itself with one reload.
 *
 * @example
 * ```ts
 * import { createSessionRenewal } from '@swimmesberger/elarion-pwa'
 *
 * const session = createSessionRenewal({ onSignedOut: () => showSignedOutBanner() })
 * export const rpc = createRpcApi({ url: '/rpc', fetch: session.fetch })
 * export const events = createElarionEvents({ url: '/events', eventSource: session.eventSource })
 * ```
 */
export function createSessionRenewal(options: SessionRenewalOptions = {}): SessionRenewal {
  const guardMs = options.reloadGuardMs ?? DEFAULT_SESSION_RENEWAL_GUARD_MS
  const storageKey = options.storageKey ?? DEFAULT_SESSION_RENEWAL_STORAGE_KEY
  const storage = options.storage === undefined ? sessionStorageOrNull() : options.storage
  const probeIntervalMs = options.probeIntervalMs ?? DEFAULT_SESSION_PROBE_INTERVAL_MS
  const reload = options.reload ?? (() => globalThis.location.reload())
  const now = options.now ?? Date.now
  const doFetch = (input: string | URL | Request, init?: RequestInit) => (options.fetch ?? globalThis.fetch)(input, init)
  const createEventSource = options.createEventSource ?? defaultEventSource

  let reloading = false
  let probing = false
  let lastProbeAt = Number.NEGATIVE_INFINITY

  function renew(): boolean {
    if (reloading) return true
    const at = now()
    try {
      if (!storage) throw new Error('No reload guard storage.')
      const last = Number(storage.getItem(storageKey))
      if (Number.isFinite(last) && at - last < guardMs) throw new Error('Renewed moments ago.')
      storage.setItem(storageKey, String(at))
    } catch {
      options.onSignedOut?.()
      return false
    }
    reloading = true
    reload()
    return true
  }

  async function sessionFetch(input: string | URL | Request, init?: RequestInit): Promise<Response> {
    const headers = new Headers(init?.headers ?? (isRequest(input) ? input.headers : undefined))
    for (const [name, value] of Object.entries(SESSION_REQUEST_HEADERS)) {
      if (!headers.has(name)) headers.set(name, value)
    }
    const response = await doFetch(input, { ...init, headers, redirect: init?.redirect ?? 'manual' })
    // The page is going away; settling would only show an error for the moment until it does.
    if (isSessionExpired(response) && renew()) return new Promise<Response>(() => {})
    return response
  }

  async function probe(url: string): Promise<void> {
    const at = now()
    if (reloading || probing || at - lastProbeAt < probeIntervalMs) return
    probing = true
    lastProbeAt = at
    const controller = new AbortController()
    try {
      const response = await doFetch(url, {
        headers: { ...SESSION_REQUEST_HEADERS, Accept: 'text/event-stream' },
        redirect: 'manual',
        credentials: 'same-origin',
        cache: 'no-store',
        signal: controller.signal,
      })
      if (isSessionExpired(response)) renew()
    } catch {
      // Offline or the server is restarting: the stream's own retries cover that.
    } finally {
      // Only the status matters; a stream that did open must not stay open.
      controller.abort()
      probing = false
    }
  }

  return {
    fetch: sessionFetch,
    eventSource(url) {
      const source = createEventSource(url)
      source.addEventListener('error', () => {
        void probe(url)
      })
      return source
    },
    renew,
  }
}

function isRequest(input: unknown): input is Request {
  return typeof Request !== 'undefined' && input instanceof Request
}

function defaultEventSource(url: string): SessionEventSource {
  const ctor = (globalThis as { EventSource?: new (url: string) => SessionEventSource }).EventSource
  if (!ctor) throw new Error('No EventSource implementation is available. Pass createEventSource.')
  return new ctor(url)
}

function sessionStorageOrNull(): ReloadGuardStorage | null {
  try {
    return globalThis.sessionStorage ?? null
  } catch {
    // Reading sessionStorage throws where storage is blocked (sandboxed frames, some privacy modes).
    return null
  }
}
