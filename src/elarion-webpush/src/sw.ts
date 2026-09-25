// The service-worker half of Elarion Web Push (ADR-0076). Import it into the application's service worker —
// through its bundler (e.g. vite-plugin-pwa's injectManifest) or a `type: 'module'` registration — and call
// registerWebPushHandlers(self) once at the top level:
//
//   import { registerWebPushHandlers } from '@swimmesberger/elarion-webpush/sw'
//   registerWebPushHandlers(self, { icon: '/icons/192.png' })
//
// It shows the `{title, body, url, tag}` payload IWebPushSender sends, focuses or opens the app at the
// payload's URL on click, and re-subscribes when the push service rotates a subscription.

import { fetchWebPushApi, toSubscriptionJson, type WebPushServerApi } from './api.js'
import { urlBase64ToUint8Array } from './base64url.js'

/** The payload `IWebPushSender` sends. */
export interface WebPushPayload {
  title: string
  body: string
  url?: string
  tag?: string
}

/** Options for {@link registerWebPushHandlers}. */
export interface WebPushServiceWorkerOptions {
  /**
   * Where a `pushsubscriptionchange` re-subscription is sent. Defaults to `fetchWebPushApi()` (the
   * `MapElarionWebPush` endpoints, authenticated by same-origin cookies). A bearer-token app, which has no
   * token in the worker, can rely on the page's `refreshOnStart` instead and pass `null` to skip it.
   */
  api?: WebPushServerApi | null
  /** The notification icon URL. */
  icon?: string
  /** The monochrome status-bar badge URL (Android). */
  badge?: string
  /** Where a click navigates when the payload has no `url`. Defaults to the service worker's scope. */
  defaultUrl?: string
  /** The title shown when a push carries no parseable payload (its text becomes the body). Defaults to 'New notification'. */
  fallbackTitle?: string
  /** Adds or overrides notification options per payload (actions, vibration, `requireInteraction`, …). */
  notificationOptions?: (payload: WebPushPayload) => NotificationOptions
}

/**
 * The parts of `ServiceWorkerGlobalScope` the handlers use — structural, so the real `self` fits without this
 * package depending on the WebWorker type library.
 */
export interface WebPushServiceWorkerScope {
  addEventListener(type: string, listener: (event: any) => void): void
  readonly registration: {
    readonly scope: string
    readonly pushManager: PushManager
    showNotification(title: string, options?: NotificationOptions): Promise<void>
  }
  readonly clients: {
    matchAll(options?: { type?: 'window'; includeUncontrolled?: boolean }): Promise<ReadonlyArray<{ readonly url: string }>>
    openWindow(url: string): Promise<unknown>
  }
}

interface WindowClientLike {
  readonly url: string
  focus(): Promise<unknown>
  navigate?(url: string): Promise<unknown>
}

interface ExtendableEventLike {
  waitUntil(promise: Promise<unknown>): void
}

interface PushEventLike extends ExtendableEventLike {
  readonly data: { text(): string } | null
}

interface NotificationEventLike extends ExtendableEventLike {
  readonly notification: { readonly data: unknown; close(): void }
}

interface PushSubscriptionChangeEventLike extends ExtendableEventLike {
  readonly oldSubscription?: PushSubscription | null
  readonly newSubscription?: PushSubscription | null
}

/** Registers the `push`, `notificationclick`, and `pushsubscriptionchange` handlers on `scope`. */
export function registerWebPushHandlers(scope: WebPushServiceWorkerScope, options: WebPushServiceWorkerOptions = {}): void {
  const api = options.api === undefined ? fetchWebPushApi() : options.api

  scope.addEventListener('push', (event: PushEventLike) => {
    const payload = parsePayload(event.data?.text(), options)
    // Always show something: a push with userVisibleOnly that displays nothing is penalized by the browser.
    event.waitUntil(
      scope.registration.showNotification(payload.title, {
        body: payload.body,
        tag: payload.tag,
        icon: options.icon,
        badge: options.badge,
        data: { url: payload.url },
        ...options.notificationOptions?.(payload),
      }),
    )
  })

  scope.addEventListener('notificationclick', (event: NotificationEventLike) => {
    event.notification.close()
    const data = event.notification.data as { url?: unknown } | null | undefined
    const relative = typeof data?.url === 'string' ? data.url : (options.defaultUrl ?? scope.registration.scope)
    event.waitUntil(focusOrOpen(scope, new URL(relative, scope.registration.scope).href))
  })

  scope.addEventListener('pushsubscriptionchange', (event: PushSubscriptionChangeEventLike) => {
    if (!api) return
    event.waitUntil(resubscribe(scope, api, event).catch(() => {
      // Nothing else to do from the worker; the page's refreshOnStart heals it on the next visit.
    }))
  })
}

function parsePayload(text: string | undefined, options: WebPushServiceWorkerOptions): WebPushPayload {
  if (text) {
    try {
      const parsed = JSON.parse(text) as Partial<WebPushPayload>
      if (typeof parsed.title === 'string') {
        return {
          title: parsed.title,
          body: typeof parsed.body === 'string' ? parsed.body : '',
          url: typeof parsed.url === 'string' ? parsed.url : undefined,
          tag: typeof parsed.tag === 'string' ? parsed.tag : undefined,
        }
      }
    } catch {
      // Not our JSON payload — show it as plain text below.
    }
  }
  return { title: options.fallbackTitle ?? 'New notification', body: text ?? '' }
}

async function focusOrOpen(scope: WebPushServiceWorkerScope, url: string): Promise<void> {
  // type: 'window' makes every match a WindowClient.
  const windows = (await scope.clients.matchAll({ type: 'window', includeUncontrolled: true })) as ReadonlyArray<WindowClientLike>
  const exact = windows.find((client) => client.url === url)
  if (exact) {
    await exact.focus()
    return
  }

  // Prefer reusing an open window of the app (one installed-PWA window) over opening a second one.
  const same = windows.find((client) => new URL(client.url).origin === new URL(url).origin && client.navigate)
  if (same?.navigate) {
    try {
      await same.navigate(url)
      await same.focus()
      return
    } catch {
      // An uncontrolled client cannot be navigated; fall through to a new window.
    }
  }
  await scope.clients.openWindow(url)
}

async function resubscribe(
  scope: WebPushServiceWorkerScope,
  api: WebPushServerApi,
  event: PushSubscriptionChangeEventLike,
): Promise<void> {
  const old = event.oldSubscription ?? null
  const replacement =
    event.newSubscription ??
    (await scope.registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: old?.options.applicationServerKey ?? urlBase64ToUint8Array(await api.getPublicKey()),
    }))
  await api.subscribe(toSubscriptionJson(replacement))
  if (old && old.endpoint !== replacement.endpoint) await api.unsubscribe(old.endpoint).catch(() => undefined)
}
