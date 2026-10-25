import { describe, expect, it } from 'vitest'
import {
  createSessionRenewal,
  DEFAULT_SESSION_PROBE_INTERVAL_MS,
  DEFAULT_SESSION_RENEWAL_GUARD_MS,
  DEFAULT_SESSION_RENEWAL_STORAGE_KEY,
  isSessionExpired,
  type ReloadGuardStorage,
  type SessionEventSource,
  type SessionRenewalOptions,
} from '../src/index.js'

// An authenticating proxy renews its session only on a full page load. Behind it, an installed app meets the expiry
// on an API call — a login redirect a fetch cannot follow, an event stream that gives up — and a single reload lets
// the proxy renew the session.

const NOW = 10_000_000

class MemoryStorage implements ReloadGuardStorage {
  readonly items = new Map<string, string>()
  getItem(key: string): string | null {
    return this.items.get(key) ?? null
  }
  setItem(key: string, value: string): void {
    this.items.set(key, value)
  }
}

class FakeEventSource implements SessionEventSource {
  readonly listeners = new Map<string, Array<(event: unknown) => void>>()
  readyState = 0
  constructor(readonly url: string) {}
  addEventListener(type: string, listener: (event: unknown) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }
  close(): void {
    this.readyState = 2
  }
  emit(type: string): void {
    for (const listener of this.listeners.get(type) ?? []) listener({})
  }
}

function opaqueRedirect(): Response {
  // The fetch spec's filtered response for redirect: 'manual' — status 0, type 'opaqueredirect'.
  return { status: 0, type: 'opaqueredirect', ok: false } as unknown as Response
}

interface Harness {
  reloads: number
  signedOut: number
  calls: Array<{ input: string | URL | Request; init?: RequestInit }>
  sources: FakeEventSource[]
  storage: MemoryStorage
  clock: { now: number }
  session: ReturnType<typeof createSessionRenewal>
}

function harness(answer: () => Response | Promise<Response>, overrides: Partial<SessionRenewalOptions> = {}): Harness {
  const h = {
    reloads: 0,
    signedOut: 0,
    calls: [] as Harness['calls'],
    sources: [] as FakeEventSource[],
    storage: new MemoryStorage(),
    clock: { now: NOW },
  }
  const session = createSessionRenewal({
    storage: h.storage,
    now: () => h.clock.now,
    reload: () => {
      h.reloads += 1
    },
    onSignedOut: () => {
      h.signedOut += 1
    },
    fetch: async (input, init) => {
      h.calls.push({ input, init })
      return answer()
    },
    createEventSource: (url) => {
      const source = new FakeEventSource(url)
      h.sources.push(source)
      return source
    },
    ...overrides,
  })
  return { ...h, session, get reloads() { return h.reloads }, get signedOut() { return h.signedOut } }
}

/** Resolves when `promise` settles within a few macrotasks, else reports it as pending. */
async function settled<T>(promise: Promise<T>): Promise<{ value: T } | 'pending'> {
  return Promise.race([promise.then((value) => ({ value })), new Promise<'pending'>((r) => setTimeout(() => r('pending'), 10))])
}

async function flush(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0))
}

describe('isSessionExpired', () => {
  it('treats a 401 and a redirect as an expired session, anything else as an answer', () => {
    expect(isSessionExpired(new Response(null, { status: 401 }))).toBe(true)
    expect(isSessionExpired(opaqueRedirect())).toBe(true)
    expect(isSessionExpired(new Response('{}', { status: 200 }))).toBe(false)
    expect(isSessionExpired(new Response(null, { status: 403 }))).toBe(false)
    expect(isSessionExpired(new Response(null, { status: 502 }))).toBe(false)
  })
})

describe('fetch', () => {
  it('marks API calls as XHR/JSON and does not follow redirects, keeping what the call set', async () => {
    const h = harness(() => new Response('{}', { status: 200 }))

    await h.session.fetch('/rpc', { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/x' } })

    const init = h.calls[0].init!
    const headers = new Headers(init.headers)
    expect(headers.get('X-Requested-With')).toBe('XMLHttpRequest')
    expect(headers.get('Accept')).toBe('application/x')
    expect(headers.get('Content-Type')).toBe('application/json')
    expect(init.redirect).toBe('manual')
    expect(init.method).toBe('POST')

    await h.session.fetch('/rpc', { redirect: 'follow' })
    expect(h.calls[1].init!.redirect).toBe('follow')
    expect(new Headers(h.calls[1].init!.headers).get('Accept')).toBe('application/json')
  })

  it('passes an ordinary answer through without reloading', async () => {
    const h = harness(() => new Response(null, { status: 403 }))

    const result = await settled(h.session.fetch('/rpc'))

    expect(result).not.toBe('pending')
    expect(h.reloads).toBe(0)
  })

  it('reloads once on a 401 and never settles, so the page goes away without showing an error', async () => {
    const h = harness(() => new Response(null, { status: 401 }))

    const first = await settled(h.session.fetch('/rpc'))
    const second = await settled(h.session.fetch('/rpc'))

    expect(first).toBe('pending')
    expect(second).toBe('pending')
    expect(h.reloads).toBe(1)
    expect(h.storage.getItem(DEFAULT_SESSION_RENEWAL_STORAGE_KEY)).toBe(String(NOW))
  })

  it('reloads on a login redirect a proxy sends despite the headers', async () => {
    const h = harness(opaqueRedirect)

    expect(await settled(h.session.fetch('/rpc'))).toBe('pending')
    expect(h.reloads).toBe(1)
  })

  it('does not reload again right after a renewal reload — it reports signed out and passes the 401 through', async () => {
    const h = harness(() => new Response(null, { status: 401 }))
    h.storage.setItem(DEFAULT_SESSION_RENEWAL_STORAGE_KEY, String(NOW - DEFAULT_SESSION_RENEWAL_GUARD_MS + 1))

    const result = await settled(h.session.fetch('/rpc'))

    expect(result).not.toBe('pending')
    expect((result as { value: Response }).value.status).toBe(401)
    expect(h.reloads).toBe(0)
    expect(h.signedOut).toBe(1)
  })

  it('reloads again once the guard window has passed', async () => {
    const h = harness(() => new Response(null, { status: 401 }))
    h.storage.setItem(DEFAULT_SESSION_RENEWAL_STORAGE_KEY, String(NOW - DEFAULT_SESSION_RENEWAL_GUARD_MS))

    expect(await settled(h.session.fetch('/rpc'))).toBe('pending')
    expect(h.reloads).toBe(1)
  })

  it('never reloads without guard storage, which could not stop a loop', async () => {
    const h = harness(() => new Response(null, { status: 401 }), { storage: null })

    expect(await settled(h.session.fetch('/rpc'))).not.toBe('pending')
    expect(h.reloads).toBe(0)
    expect(h.signedOut).toBe(1)
  })

  it('treats storage that throws like no storage', async () => {
    const h = harness(() => new Response(null, { status: 401 }), {
      storage: {
        getItem: () => {
          throw new Error('blocked')
        },
        setItem: () => undefined,
      },
    })

    expect(await settled(h.session.fetch('/rpc'))).not.toBe('pending')
    expect(h.reloads).toBe(0)
  })
})

describe('eventSource', () => {
  it('probes the stream URL on an error and renews when the session expired', async () => {
    const h = harness(() => new Response(null, { status: 401 }))
    const source = h.session.eventSource('/events?subscriptions=x')

    source satisfies SessionEventSource
    h.sources[0].emit('error')
    await flush()

    expect(h.calls).toHaveLength(1)
    expect(h.calls[0].input).toBe('/events?subscriptions=x')
    const init = h.calls[0].init!
    expect(new Headers(init.headers).get('X-Requested-With')).toBe('XMLHttpRequest')
    expect(new Headers(init.headers).get('Accept')).toBe('text/event-stream')
    expect(init.redirect).toBe('manual')
    expect(init.signal?.aborted).toBe(true)
    expect(h.reloads).toBe(1)
  })

  it('leaves a stream that is still authorized to its own retries', async () => {
    const h = harness(() => new Response('', { status: 200, headers: { 'Content-Type': 'text/event-stream' } }))
    h.session.eventSource('/events')

    h.sources[0].emit('error')
    await flush()

    expect(h.calls).toHaveLength(1)
    expect(h.calls[0].init!.signal?.aborted).toBe(true)
    expect(h.reloads).toBe(0)
  })

  it('ignores a probe that fails on the network', async () => {
    const h = harness(() => Promise.reject(new TypeError('Failed to fetch')))
    h.session.eventSource('/events')

    h.sources[0].emit('error')
    await flush()

    expect(h.reloads).toBe(0)
    expect(h.signedOut).toBe(0)
  })

  it('probes at most once per interval however often the stream reports an error', async () => {
    const h = harness(() => new Response(null, { status: 200 }))
    h.session.eventSource('/events')

    h.sources[0].emit('error')
    h.sources[0].emit('error')
    await flush()
    h.sources[0].emit('error')
    await flush()
    expect(h.calls).toHaveLength(1)

    h.clock.now += DEFAULT_SESSION_PROBE_INTERVAL_MS
    h.sources[0].emit('error')
    await flush()
    expect(h.calls).toHaveLength(2)
  })
})
