/** A browser subscription as `PushSubscription.toJSON()` serializes it — the body the server stores. */
export interface WebPushSubscriptionJson {
  endpoint: string
  expirationTime?: number | null
  keys: { p256dh: string; auth: string }
}

/**
 * The three server calls the helpers need. Use {@link fetchWebPushApi} with `MapElarionWebPush()`, or adapt the
 * generated JSON-RPC client when the application exposes its own `webPush.*` handlers:
 *
 * ```ts
 * const api: WebPushServerApi = {
 *   getPublicKey: async () => (await rpc.webPush.publicKey({})).publicKey,
 *   subscribe: async (subscription) => { await rpc.webPush.subscribe(subscription) },
 *   unsubscribe: async (endpoint) => { await rpc.webPush.unsubscribe({ endpoint }) },
 * }
 * ```
 */
export interface WebPushServerApi {
  /** The VAPID public key (base64url) to subscribe against. */
  getPublicKey(): Promise<string>
  /** Stores the subscription for the signed-in user (idempotent). */
  subscribe(subscription: WebPushSubscriptionJson): Promise<void>
  /** Deletes the signed-in user's subscription for the endpoint. */
  unsubscribe(endpoint: string): Promise<void>
}

/** Options for {@link fetchWebPushApi}. */
export interface FetchWebPushApiOptions {
  /** The prefix `MapElarionWebPush` was mapped at. Defaults to `/webpush`. */
  baseUrl?: string
  /** Extra headers per request, e.g. a bearer token. Cookies are sent same-origin by default. */
  headers?: () => HeadersInit | Promise<HeadersInit>
  /** The `fetch` implementation. Defaults to the global one. */
  fetch?: typeof fetch
}

/** A {@link WebPushServerApi} over the endpoints `app.MapElarionWebPush()` maps. */
export function fetchWebPushApi(options: FetchWebPushApiOptions = {}): WebPushServerApi {
  const baseUrl = (options.baseUrl ?? '/webpush').replace(/\/+$/, '')

  async function call(path: string, body?: unknown): Promise<Response> {
    const doFetch = options.fetch ?? globalThis.fetch
    const headers = new Headers(options.headers ? await options.headers() : undefined)
    if (body !== undefined) headers.set('Content-Type', 'application/json')
    const response = await doFetch(`${baseUrl}${path}`, {
      method: body === undefined ? 'GET' : 'POST',
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: 'same-origin',
    })
    if (!response.ok) throw new Error(`Web Push ${path} failed with HTTP ${response.status}.`)
    return response
  }

  return {
    async getPublicKey() {
      const { publicKey } = (await (await call('/public-key')).json()) as { publicKey: string }
      return publicKey
    },
    async subscribe(subscription) {
      await call('/subscribe', subscription)
    },
    async unsubscribe(endpoint) {
      await call('/unsubscribe', { endpoint })
    },
  }
}

/** Serializes a live subscription, failing loudly if the browser omitted its keys. */
export function toSubscriptionJson(subscription: PushSubscription): WebPushSubscriptionJson {
  const json = subscription.toJSON()
  const p256dh = json.keys?.['p256dh']
  const auth = json.keys?.['auth']
  if (!json.endpoint || !p256dh || !auth) throw new Error('The push subscription has no endpoint or keys.')
  return { endpoint: json.endpoint, expirationTime: json.expirationTime ?? null, keys: { p256dh, auth } }
}
