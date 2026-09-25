import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  enablePush,
  fetchWebPushApi,
  isSubscribed,
  pushAvailability,
  refreshOnStart,
  subscribe,
  unsubscribe,
  urlBase64ToUint8Array,
  type WebPushServerApi,
  type WebPushSubscriptionJson,
} from '../src/index.js'

const KEY_A = 'BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8'
const KEY_B = 'BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4'

const CHROME = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36'
const IPHONE_18 = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1'
const IPHONE_16_3 = 'Mozilla/5.0 (iPhone; CPU iPhone OS 16_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.3 Mobile/15E148 Safari/604.1'
const IPAD_DESKTOP = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Safari/605.1.15'

class FakeSubscription {
  unsubscribed = false
  constructor(
    readonly endpoint: string,
    readonly options: { applicationServerKey: ArrayBuffer | null },
    private readonly manager: FakePushManager,
  ) {}

  toJSON() {
    return { endpoint: this.endpoint, expirationTime: null, keys: { p256dh: 'p256dh-' + this.endpoint.slice(-1), auth: 'auth' } }
  }

  async unsubscribe() {
    this.unsubscribed = true
    if (this.manager.current === this) this.manager.current = null
    return true
  }
}

class FakePushManager {
  current: FakeSubscription | null = null
  created = 0

  async getSubscription() {
    return this.current
  }

  async subscribe(options: { userVisibleOnly: boolean; applicationServerKey: Uint8Array }) {
    expect(options.userVisibleOnly).toBe(true)
    const key = options.applicationServerKey
    this.current = new FakeSubscription(
      `https://fcm.googleapis.com/fcm/send/${++this.created}`,
      { applicationServerKey: key.buffer.slice(key.byteOffset, key.byteOffset + key.byteLength) as ArrayBuffer },
      this,
    )
    return this.current
  }
}

interface BrowserSetup {
  userAgent?: string
  platform?: string
  maxTouchPoints?: number
  standalone?: boolean
  push?: boolean
  permission?: NotificationPermission
  requestResult?: NotificationPermission
}

function fakeBrowser(setup: BrowserSetup = {}) {
  const pushManager = new FakePushManager()
  const registration = { pushManager } as unknown as ServiceWorkerRegistration
  const push = setup.push ?? true
  const requestPermission = vi.fn(async () => {
    notification.permission = setup.requestResult ?? 'granted'
    return notification.permission
  })
  const notification = { permission: setup.permission ?? 'default', requestPermission }
  vi.stubGlobal('navigator', {
    userAgent: setup.userAgent ?? CHROME,
    platform: setup.platform ?? 'Win32',
    maxTouchPoints: setup.maxTouchPoints ?? 0,
    standalone: setup.standalone,
    ...(push ? { serviceWorker: { ready: Promise.resolve(registration) } } : {}),
  })
  vi.stubGlobal('window', {
    ...(push ? { PushManager: class {}, Notification: notification } : {}),
    matchMedia: () => ({ matches: setup.standalone ?? false }),
  })
  vi.stubGlobal('Notification', notification)
  return { pushManager, registration, requestPermission }
}

function fakeApi(publicKey = KEY_A) {
  const subscribed: WebPushSubscriptionJson[] = []
  const unsubscribed: string[] = []
  const api: WebPushServerApi = {
    getPublicKey: vi.fn(async () => publicKey),
    subscribe: vi.fn(async (subscription) => {
      subscribed.push(subscription)
    }),
    unsubscribe: vi.fn(async (endpoint) => {
      unsubscribed.push(endpoint)
    }),
  }
  return { api, subscribed, unsubscribed }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('urlBase64ToUint8Array', () => {
  it('decodes an unpadded base64url VAPID key to its 65 bytes', () => {
    const bytes = urlBase64ToUint8Array(KEY_A)
    expect(bytes).toHaveLength(65)
    expect(bytes[0]).toBe(0x04)
    const roundTrip = btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
    expect(roundTrip).toBe(KEY_A)
  })
})

describe('pushAvailability', () => {
  it('is available where service workers, PushManager, and Notification exist', () => {
    fakeBrowser()
    expect(pushAvailability()).toBe('available')
  })

  it('asks iOS Safari outside the Home Screen to install first', () => {
    fakeBrowser({ userAgent: IPHONE_18, push: false })
    expect(pushAvailability()).toBe('install-first')
  })

  it('recognizes iPadOS presenting a desktop user agent', () => {
    fakeBrowser({ userAgent: IPAD_DESKTOP, platform: 'MacIntel', maxTouchPoints: 5, push: false })
    expect(pushAvailability()).toBe('install-first')
  })

  it('is unsupported on iOS before 16.4 even once installed', () => {
    fakeBrowser({ userAgent: IPHONE_16_3, push: false })
    expect(pushAvailability()).toBe('unsupported')
  })

  it('is unsupported on a Home Screen app that still lacks PushManager', () => {
    fakeBrowser({ userAgent: IPHONE_18, push: false, standalone: true })
    expect(pushAvailability()).toBe('unsupported')
  })

  it('is unsupported on a desktop browser without Web Push', () => {
    fakeBrowser({ push: false })
    expect(pushAvailability()).toBe('unsupported')
  })
})

describe('enablePush', () => {
  it('requests permission synchronously inside the gesture, then subscribes', async () => {
    const { requestPermission, pushManager } = fakeBrowser()
    const { api, subscribed } = fakeApi()

    const outcome = enablePush(api)
    // Before any await: Safari rejects a permission request made after the gesture's task.
    expect(requestPermission).toHaveBeenCalledTimes(1)

    await expect(outcome).resolves.toBe('subscribed')
    expect(pushManager.current).not.toBeNull()
    expect(subscribed).toEqual([pushManager.current!.toJSON()])
  })

  it('reports a denied or dismissed prompt without subscribing', async () => {
    fakeBrowser({ requestResult: 'denied' })
    const denied = fakeApi()
    await expect(enablePush(denied.api)).resolves.toBe('denied')

    fakeBrowser({ requestResult: 'default' })
    const dismissed = fakeApi()
    await expect(enablePush(dismissed.api)).resolves.toBe('dismissed')

    expect(denied.subscribed).toEqual([])
    expect(dismissed.subscribed).toEqual([])
  })

  it('does not prompt when push cannot work here', async () => {
    const { requestPermission } = fakeBrowser({ userAgent: IPHONE_18, push: false })
    await expect(enablePush(fakeApi().api)).resolves.toBe('install-first')
    expect(requestPermission).not.toHaveBeenCalled()
  })
})

describe('subscribe and refreshOnStart', () => {
  it('reuses a subscription for the current key and re-sends it to the server', async () => {
    const { pushManager } = fakeBrowser({ permission: 'granted' })
    const { api, subscribed } = fakeApi()
    const first = await subscribe(api)

    await expect(refreshOnStart(api)).resolves.toBe(true)

    expect(pushManager.created).toBe(1)
    expect(subscribed).toEqual([first.toJSON(), first.toJSON()])
  })

  it('replaces a subscription made for a rotated server key', async () => {
    const { pushManager } = fakeBrowser({ permission: 'granted' })
    const old = await subscribe(fakeApi(KEY_B).api)
    const { api, subscribed } = fakeApi(KEY_A)

    await refreshOnStart(api)

    expect((old as unknown as FakeSubscription).unsubscribed).toBe(true)
    expect(pushManager.created).toBe(2)
    expect(subscribed).toEqual([pushManager.current!.toJSON()])
  })

  it('never prompts or subscribes without granted permission', async () => {
    const { requestPermission, pushManager } = fakeBrowser({ permission: 'default' })
    await expect(refreshOnStart(fakeApi().api)).resolves.toBe(false)
    expect(requestPermission).not.toHaveBeenCalled()
    expect(pushManager.current).toBeNull()
  })

  it('uses an explicit registration instead of waiting for serviceWorker.ready', async () => {
    fakeBrowser({ permission: 'granted' })
    const explicit = new FakePushManager()
    await subscribe(fakeApi().api, { registration: { pushManager: explicit } as unknown as ServiceWorkerRegistration })
    expect(explicit.current).not.toBeNull()
  })
})

describe('unsubscribe and isSubscribed', () => {
  it('removes the subscription on the server, then in the browser', async () => {
    const { pushManager } = fakeBrowser({ permission: 'granted' })
    const { api, unsubscribed } = fakeApi()
    const subscription = await subscribe(api)
    await expect(isSubscribed()).resolves.toBe(true)

    await expect(unsubscribe(api)).resolves.toBe(true)

    expect(unsubscribed).toEqual([subscription.endpoint])
    expect(pushManager.current).toBeNull()
    await expect(isSubscribed()).resolves.toBe(false)
    await expect(unsubscribe(api)).resolves.toBe(false)
  })

  it('keeps the browser subscribed when the server call fails', async () => {
    const { pushManager } = fakeBrowser({ permission: 'granted' })
    const { api } = fakeApi()
    await subscribe(api)
    api.unsubscribe = vi.fn(async () => {
      throw new Error('offline')
    })

    await expect(unsubscribe(api)).rejects.toThrow('offline')
    expect(pushManager.current).not.toBeNull()
  })
})

describe('fetchWebPushApi', () => {
  it('calls the MapElarionWebPush endpoints with same-origin credentials and custom headers', async () => {
    const calls: { url: string; init: RequestInit }[] = []
    const fetchStub = vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
      calls.push({ url: String(url), init: init! })
      return String(url).endsWith('/public-key')
        ? new Response(JSON.stringify({ publicKey: KEY_A }), { status: 200 })
        : new Response(null, { status: 204 })
    })
    const api = fetchWebPushApi({ baseUrl: '/api/push/', fetch: fetchStub, headers: () => ({ Authorization: 'Bearer t' }) })
    const subscription = { endpoint: 'https://fcm.googleapis.com/fcm/send/1', keys: { p256dh: 'p', auth: 'a' } }

    await expect(api.getPublicKey()).resolves.toBe(KEY_A)
    await api.subscribe(subscription)
    await api.unsubscribe(subscription.endpoint)

    expect(calls.map((call) => [call.init.method, call.url])).toEqual([
      ['GET', '/api/push/public-key'],
      ['POST', '/api/push/subscribe'],
      ['POST', '/api/push/unsubscribe'],
    ])
    expect(JSON.parse(calls[1].init.body as string)).toEqual(subscription)
    expect(JSON.parse(calls[2].init.body as string)).toEqual({ endpoint: subscription.endpoint })
    expect(calls.every((call) => call.init.credentials === 'same-origin')).toBe(true)
    expect(new Headers(calls[1].init.headers).get('Authorization')).toBe('Bearer t')
    expect(new Headers(calls[1].init.headers).get('Content-Type')).toBe('application/json')
  })

  it('throws on a non-success status', async () => {
    const api = fetchWebPushApi({ fetch: async () => new Response(null, { status: 401 }) })
    await expect(api.subscribe({ endpoint: 'e', keys: { p256dh: 'p', auth: 'a' } })).rejects.toThrow('HTTP 401')
  })
})
