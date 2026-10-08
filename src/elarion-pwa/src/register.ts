// Registering the service worker. Small, but the defaults matter: `updateViaCache: 'none'` makes the browser's
// update check for the worker script skip the HTTP cache, so a changed worker is picked up on the next start even
// when a proxy ignored the script's `Cache-Control: no-cache`.

/** The part of `navigator.serviceWorker` {@link registerServiceWorker} uses. */
export interface ServiceWorkerContainerLike {
  register(scriptURL: string | URL, options?: RegistrationOptions): Promise<ServiceWorkerRegistration>
}

/** Options for {@link registerServiceWorker}. */
export interface RegisterServiceWorkerOptions {
  /** The registration scope. Defaults to the script's directory — `/` for `/sw.js`. */
  scope?: string
  /** `module` for an unbundled ESM worker. Defaults to `classic`, which every browser supports. */
  type?: WorkerType
  /** Defaults to `'none'`: the worker script and its imports are always fetched past the HTTP cache. */
  updateViaCache?: ServiceWorkerUpdateViaCache
  /** Called when registration fails; the app keeps working without an installable shell. */
  onError?: (error: unknown) => void
  /** The container to register with. Defaults to `navigator.serviceWorker`. */
  container?: ServiceWorkerContainerLike
}

/**
 * Registers the service worker and resolves to its registration — or `null` where service workers are unavailable
 * (an old browser, a non-secure context, a private mode that disables them) or registration failed.
 *
 * Gate it on a production build yourself: under a dev server a worker would sit between the browser and the modules
 * hot reload is about to replace.
 *
 * @example
 * ```ts
 * if (import.meta.env.PROD) void registerServiceWorker('/sw.js')
 * ```
 */
export async function registerServiceWorker(
  url: string | URL = '/sw.js',
  options: RegisterServiceWorkerOptions = {},
): Promise<ServiceWorkerRegistration | null> {
  const container =
    options.container ?? (typeof navigator !== 'undefined' && 'serviceWorker' in navigator ? navigator.serviceWorker : undefined)
  if (!container) return null
  try {
    return await container.register(url, {
      scope: options.scope,
      type: options.type,
      updateViaCache: options.updateViaCache ?? 'none',
    })
  } catch (error) {
    options.onError?.(error)
    return null
  }
}
