import { describe, expect, it, vi } from 'vitest'
import type { WebPushServerApi, WebPushSubscriptionJson } from '../src/index.js'
import { registerWebPushHandlers, type WebPushServiceWorkerOptions, type WebPushServiceWorkerScope } from '../src/sw.js'

const SCOPE = 'https://app.example/'

interface FakeClient {
  url: string
  focus: ReturnType<typeof vi.fn>
  navigate?: ReturnType<typeof vi.fn>
}

function fakeWorker(clients: FakeClient[] = [], options: WebPushServiceWorkerOptions = {}) {
  const listeners = new Map<string, (event: any) => void>()
  const shown: { title: string; options?: NotificationOptions }[] = []
  const opened: string[] = []
  const subscribeCalls: unknown[] = []
  const scope: WebPushServiceWorkerScope = {
    addEventListener: (type, listener) => listeners.set(type, listener),
    registration: {
      scope: SCOPE,
      pushManager: {
        subscribe: async (init: PushSubscriptionOptionsInit) => {
          subscribeCalls.push(init)
          return fakeSubscription('https://fcm.googleapis.com/fcm/send/new')
        },
      } as unknown as PushManager,
      showNotification: async (title, notificationOptions) => {
        shown.push({ title, options: notificationOptions })
      },
    },
    clients: {
      matchAll: async () => clients,
      openWindow: async (url) => {
        opened.push(url)
      },
    },
  }
  registerWebPushHandlers(scope, options)

  async function dispatch(type: string, event: object) {
    const pending: Promise<unknown>[] = []
    listeners.get(type)!({ ...event, waitUntil: (promise: Promise<unknown>) => pending.push(promise) })
    await Promise.all(pending)
  }

  return { dispatch, shown, opened, subscribeCalls }
}

function fakeSubscription(endpoint: string, key: ArrayBuffer | null = null): PushSubscription {
  return {
    endpoint,
    options: { applicationServerKey: key },
    toJSON: () => ({ endpoint, keys: { p256dh: 'p', auth: 'a' } }),
  } as unknown as PushSubscription
}

function pushData(text: string) {
  return { data: { text: () => text } }
}

function clickEvent(url?: string) {
  return { notification: { data: { url }, close: vi.fn() } }
}

describe('push', () => {
  it('shows the payload as a notification carrying its url and tag', async () => {
    const worker = fakeWorker([], { icon: '/icon.png', notificationOptions: () => ({ requireInteraction: true }) })

    await worker.dispatch('push', pushData(JSON.stringify({ title: 'Deploy failed', body: 'api', url: '/deploys/1', tag: 'd1' })))

    expect(worker.shown).toEqual([
      {
        title: 'Deploy failed',
        options: {
          body: 'api',
          tag: 'd1',
          icon: '/icon.png',
          badge: undefined,
          data: { url: '/deploys/1' },
          requireInteraction: true,
        },
      },
    ])
  })

  it('still shows something for a payload it does not understand', async () => {
    const worker = fakeWorker([], { fallbackTitle: 'My App' })

    await worker.dispatch('push', pushData('plain text'))
    await worker.dispatch('push', { data: null })

    expect(worker.shown.map((notification) => [notification.title, notification.options?.body])).toEqual([
      ['My App', 'plain text'],
      ['My App', ''],
    ])
  })
})

describe('notificationclick', () => {
  it('focuses a window already at the url', async () => {
    const exact: FakeClient = { url: 'https://app.example/deploys/1', focus: vi.fn(async () => undefined) }
    const worker = fakeWorker([exact])
    const event = clickEvent('/deploys/1')

    await worker.dispatch('notificationclick', event)

    expect(event.notification.close).toHaveBeenCalled()
    expect(exact.focus).toHaveBeenCalled()
    expect(worker.opened).toEqual([])
  })

  it('navigates an open app window instead of opening a second one', async () => {
    const other: FakeClient = {
      url: 'https://app.example/home',
      focus: vi.fn(async () => undefined),
      navigate: vi.fn(async () => undefined),
    }
    const worker = fakeWorker([other])

    await worker.dispatch('notificationclick', clickEvent('/deploys/1'))

    expect(other.navigate).toHaveBeenCalledWith('https://app.example/deploys/1')
    expect(other.focus).toHaveBeenCalled()
    expect(worker.opened).toEqual([])
  })

  it('opens the scope when no window is open and the payload has no url', async () => {
    const worker = fakeWorker([])

    await worker.dispatch('notificationclick', clickEvent())

    expect(worker.opened).toEqual([SCOPE])
  })
})

describe('pushsubscriptionchange', () => {
  function recordingApi() {
    const subscribed: WebPushSubscriptionJson[] = []
    const unsubscribed: string[] = []
    const api: WebPushServerApi = {
      getPublicKey: async () => 'BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8',
      subscribe: async (subscription) => {
        subscribed.push(subscription)
      },
      unsubscribe: async (endpoint) => {
        unsubscribed.push(endpoint)
      },
    }
    return { api, subscribed, unsubscribed }
  }

  it('stores the replacement the browser supplied and drops the old endpoint', async () => {
    const { api, subscribed, unsubscribed } = recordingApi()
    const worker = fakeWorker([], { api })

    await worker.dispatch('pushsubscriptionchange', {
      oldSubscription: fakeSubscription('https://fcm.googleapis.com/fcm/send/old'),
      newSubscription: fakeSubscription('https://fcm.googleapis.com/fcm/send/rotated'),
    })

    expect(worker.subscribeCalls).toEqual([])
    expect(subscribed.map((subscription) => subscription.endpoint)).toEqual(['https://fcm.googleapis.com/fcm/send/rotated'])
    expect(unsubscribed).toEqual(['https://fcm.googleapis.com/fcm/send/old'])
  })

  it('re-subscribes with the old key when the browser supplied no replacement', async () => {
    const { api, subscribed } = recordingApi()
    const worker = fakeWorker([], { api })
    const oldKey = new Uint8Array([4, 1, 2]).buffer

    await worker.dispatch('pushsubscriptionchange', {
      oldSubscription: fakeSubscription('https://fcm.googleapis.com/fcm/send/old', oldKey),
      newSubscription: null,
    })

    expect(worker.subscribeCalls).toEqual([{ userVisibleOnly: true, applicationServerKey: oldKey }])
    expect(subscribed.map((subscription) => subscription.endpoint)).toEqual(['https://fcm.googleapis.com/fcm/send/new'])
  })

  it('does nothing when resync is disabled', async () => {
    const worker = fakeWorker([], { api: null })

    await worker.dispatch('pushsubscriptionchange', { oldSubscription: null, newSubscription: null })

    expect(worker.subscribeCalls).toEqual([])
  })
})
