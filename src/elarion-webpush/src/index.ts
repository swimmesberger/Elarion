// The browser half of Elarion Web Push (ADR-0076): detect whether this browser can receive pushes at all,
// ask for permission inside the user gesture, and keep the server's copy of the subscription in step with
// the browser's. Server calls go through a pluggable WebPushServerApi, so the same helpers work against
// `MapElarionWebPush()` or an application's own `webPush.*` handlers via the generated JSON-RPC client.
//
// The service-worker half (showing the notification, handling its click, and re-subscribing when the push
// service rotates a subscription) is the `/sw` sub-export.

import { toSubscriptionJson, type WebPushServerApi } from './api.js'
import { isSameKey, urlBase64ToUint8Array } from './base64url.js'

export {
  fetchWebPushApi,
  toSubscriptionJson,
  type FetchWebPushApiOptions,
  type WebPushServerApi,
  type WebPushSubscriptionJson,
} from './api.js'
export { urlBase64ToUint8Array } from './base64url.js'

/**
 * Whether this browser can receive pushes:
 * - `available` — offer the switch.
 * - `install-first` — iOS/iPadOS Safari outside a Home Screen app, where `PushManager` does not exist until
 *   the site is added to the Home Screen (iOS 16.4+). Say "add to Home Screen first" instead of offering a
 *   switch that can never turn on.
 * - `unsupported` — no Web Push here (an old browser or iOS before 16.4, a non-secure context, …).
 */
export type PushAvailability = 'available' | 'install-first' | 'unsupported'

/** What {@link enablePush} achieved. `dismissed` means the prompt was closed without a choice — it may be offered again. */
export type EnablePushOutcome = 'subscribed' | 'denied' | 'dismissed' | 'install-first' | 'unsupported'

/** Options shared by the subscription helpers. */
export interface PushHelperOptions {
  /**
   * The service-worker registration to subscribe through. Defaults to `navigator.serviceWorker.ready` — which
   * never settles when no service worker is registered, so register one before calling the helpers.
   */
  registration?: ServiceWorkerRegistration
}

/** Detects push support; see {@link PushAvailability}. Safe to call during rendering. */
export function pushAvailability(): PushAvailability {
  if (typeof navigator === 'undefined' || typeof window === 'undefined') return 'unsupported'
  if ('serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window) return 'available'

  const ios = iosVersion()
  if (ios === undefined || isStandalone()) return 'unsupported'
  // Web Push reached iOS/iPadOS in 16.4, and only for Home Screen apps.
  return ios[0] > 16 || (ios[0] === 16 && ios[1] >= 4) ? 'install-first' : 'unsupported'
}

/**
 * Asks for notification permission and subscribes. Call it directly from a click handler: the permission
 * request is issued before the first `await`, because Safari only honours one made synchronously inside the
 * user gesture.
 */
export function enablePush(api: WebPushServerApi, options: PushHelperOptions = {}): Promise<EnablePushOutcome> {
  const availability = pushAvailability()
  if (availability !== 'available') return Promise.resolve(availability)

  const permission: Promise<NotificationPermission> =
    Notification.permission === 'granted' ? Promise.resolve('granted') : Notification.requestPermission()
  return permission.then(async (result) => {
    if (result === 'denied') return 'denied'
    if (result !== 'granted') return 'dismissed'
    await subscribe(api, options)
    return 'subscribed'
  })
}

/**
 * Subscribes this browser (permission must already be granted) and stores the subscription on the server.
 * An existing subscription for the same server key is reused; one for a rotated key is replaced.
 */
export async function subscribe(api: WebPushServerApi, options: PushHelperOptions = {}): Promise<PushSubscription> {
  const registration = await resolveRegistration(options)
  const key = urlBase64ToUint8Array(await api.getPublicKey())
  let subscription = await registration.pushManager.getSubscription()
  if (subscription && !isSameKey(subscription.options.applicationServerKey, key)) {
    await subscription.unsubscribe()
    subscription = null
  }

  subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key })
  await api.subscribe(toSubscriptionJson(subscription))
  return subscription
}

/**
 * Removes this browser's subscription — on the server first, so a failed call leaves both sides subscribed
 * rather than the server holding a dead subscription. Returns `false` when there was none.
 */
export async function unsubscribe(api: WebPushServerApi, options: PushHelperOptions = {}): Promise<boolean> {
  if (pushAvailability() !== 'available') return false
  const registration = await resolveRegistration(options)
  const subscription = await registration.pushManager.getSubscription()
  if (!subscription) return false
  await api.unsubscribe(subscription.endpoint)
  await subscription.unsubscribe()
  return true
}

/** Whether this browser currently holds a push subscription with notification permission granted. */
export async function isSubscribed(options: PushHelperOptions = {}): Promise<boolean> {
  if (pushAvailability() !== 'available' || Notification.permission !== 'granted') return false
  const registration = await resolveRegistration(options)
  return (await registration.pushManager.getSubscription()) !== null
}

/**
 * Call once on app start. When the user has granted permission, re-sends (or re-creates) the subscription so
 * the server's copy heals after the push service rotated it, the server key changed, or a cleanup deleted
 * it — and so its last-seen time stays fresh. Never prompts. Returns whether a subscription was refreshed.
 */
export async function refreshOnStart(api: WebPushServerApi, options: PushHelperOptions = {}): Promise<boolean> {
  if (pushAvailability() !== 'available' || Notification.permission !== 'granted') return false
  await subscribe(api, options)
  return true
}

async function resolveRegistration(options: PushHelperOptions): Promise<ServiceWorkerRegistration> {
  return options.registration ?? navigator.serviceWorker.ready
}

function iosVersion(): [number, number] | undefined {
  const userAgent = navigator.userAgent
  const match = /(?:iPhone|iPad|iPod).* OS (\d+)_(\d+)/.exec(userAgent)
  if (match) return [Number(match[1]), Number(match[2])]
  // iPadOS 13+ Safari presents a desktop Mac user agent; a touch-capable "Mac" is an iPad.
  if (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1) {
    const version = /Version\/(\d+)\.(\d+)/.exec(userAgent)
    return version ? [Number(version[1]), Number(version[2])] : undefined
  }
  return undefined
}

function isStandalone(): boolean {
  return (
    (navigator as Navigator & { standalone?: boolean }).standalone === true ||
    (typeof window.matchMedia === 'function' && window.matchMedia('(display-mode: standalone)').matches)
  )
}
