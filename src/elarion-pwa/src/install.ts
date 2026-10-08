// Installing the app: which way this browser offers right now — its own install prompt (`beforeinstallprompt`,
// Chromium), the share-sheet instructions (iPhone/iPad, which has no prompt API), or nothing (already installed,
// or a browser that cannot install a web app) — and the deferred prompt itself. The decision is pure so it can be
// unit-tested; the controller feeds it the live browser state and notifies subscribers when it changes.

/** What the browser says about itself. */
export interface InstallEnvironment {
  readonly userAgent: string
  /** `navigator.maxTouchPoints` — the only tell of an iPad, which reports a desktop Mac user agent. */
  readonly maxTouchPoints: number
  /** Already running as an installed app (`display-mode: standalone`, or iOS's `navigator.standalone`). */
  readonly standalone: boolean
}

/**
 * How this browser can install the app right now:
 * - `prompt` — the browser handed over its install prompt; offer an "Install app" action that calls
 *   {@link InstallPromptController.prompt}.
 * - `ios` — iPhone/iPad: no prompt API; show the "Share → Add to Home Screen" instructions.
 * - `none` — already installed, or a browser that cannot install a web app (Firefox desktop, an in-app browser,
 *   Chromium before the page qualifies). Hide the entry rather than offering a dead end.
 */
export type InstallOption = 'prompt' | 'ios' | 'none'

/** What {@link InstallPromptController.prompt} achieved. */
export type InstallPromptOutcome = 'accepted' | 'dismissed' | 'unavailable'

/**
 * iPhone, iPod or iPad. iPadOS 13+ presents a Mac user agent, so a "Mac" with a touch screen counts too — no real
 * Mac has one. Every iOS browser is WebKit and, since iOS 16.4, all of them offer "Add to Home Screen" in their
 * share menu, so this deliberately does not single out Safari.
 */
export function isAppleTouchDevice(userAgent: string, maxTouchPoints: number): boolean {
  if (/\b(iPhone|iPad|iPod)\b/.test(userAgent)) return true
  return /\bMacintosh\b/.test(userAgent) && maxTouchPoints > 1
}

/** Whether the page runs as an installed app rather than in a browser tab. `false` outside a browser. */
export function isStandalone(): boolean {
  if (typeof window === 'undefined' || typeof navigator === 'undefined') return false
  if ((navigator as Navigator & { standalone?: boolean }).standalone === true) return true
  return typeof window.matchMedia === 'function' && window.matchMedia('(display-mode: standalone)').matches
}

/** The live {@link InstallEnvironment}, or `null` outside a browser (server rendering, tests without a DOM). */
export function readInstallEnvironment(): InstallEnvironment | null {
  if (typeof window === 'undefined' || typeof navigator === 'undefined') return null
  return { userAgent: navigator.userAgent, maxTouchPoints: navigator.maxTouchPoints ?? 0, standalone: isStandalone() }
}

/** How this browser can install the app, given whether a deferred install prompt is in hand. */
export function installOption(env: InstallEnvironment | null, canPrompt: boolean): InstallOption {
  if (env === null || env.standalone) return 'none'
  if (canPrompt) return 'prompt'
  if (isAppleTouchDevice(env.userAgent, env.maxTouchPoints)) return 'ios'
  return 'none'
}

/** Chromium's install prompt event — never standardized, so not in the DOM typings. */
interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<unknown>
  readonly userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

/** Options for {@link createInstallPrompt}. */
export interface InstallPromptOptions {
  /** Where `beforeinstallprompt` and `appinstalled` are listened for. Defaults to `window`. */
  target?: EventTarget
  /** Reads the browser state. Defaults to {@link readInstallEnvironment}. */
  environment?: () => InstallEnvironment | null
  /**
   * Suppress Chromium's own install mini-infobar so the app offers installing where users look for it. Defaults to
   * `true`; pass `false` to keep the browser's UI as well.
   */
  preventDefault?: boolean
}

/** The deferred install prompt and the install option derived from it; see {@link createInstallPrompt}. */
export interface InstallPromptController {
  /**
   * The current {@link InstallOption}. A string snapshot, so it can back React's `useSyncExternalStore` directly
   * (pass `() => 'none'` as the server snapshot: the environment is unknown while rendering on the server).
   */
  availability(): InstallOption
  /** Calls `listener` whenever {@link availability} may have changed. Returns the unsubscribe function. */
  subscribe(listener: () => void): () => void
  /**
   * Shows the browser's install prompt. Call it from a click handler. A prompt can be shown once: whatever the
   * answer, the event is spent and {@link availability} drops to `none` until the browser offers another one.
   */
  prompt(): Promise<InstallPromptOutcome>
  /** Stops listening. */
  dispose(): void
}

/**
 * Captures the browser's install prompt and tracks how the app can be installed.
 *
 * Create it **once, at module load** of the client bundle (not inside a component effect): Chromium fires
 * `beforeinstallprompt` once, as soon as the page qualifies, which can be before the UI framework has mounted.
 *
 * @example
 * ```ts
 * export const install = createInstallPrompt()
 *
 * // React
 * const option = useSyncExternalStore(install.subscribe, install.availability, () => 'none')
 * if (option === 'prompt') return <button onClick={() => install.prompt()}>Install app</button>
 * if (option === 'ios') return <p>Tap Share, then “Add to Home Screen”.</p>
 * ```
 */
export function createInstallPrompt(options: InstallPromptOptions = {}): InstallPromptController {
  const target = options.target ?? (typeof window === 'undefined' ? undefined : window)
  const environment = options.environment ?? readInstallEnvironment
  const listeners = new Set<() => void>()
  let deferred: BeforeInstallPromptEvent | null = null

  const notify = () => {
    for (const listener of [...listeners]) listener()
  }
  const onBeforeInstallPrompt = (event: Event) => {
    if (options.preventDefault !== false) event.preventDefault()
    deferred = event as BeforeInstallPromptEvent
    notify()
  }
  const onAppInstalled = () => {
    deferred = null
    notify()
  }
  target?.addEventListener('beforeinstallprompt', onBeforeInstallPrompt)
  target?.addEventListener('appinstalled', onAppInstalled)

  return {
    availability: () => installOption(target ? environment() : null, deferred !== null),
    subscribe(listener) {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    async prompt() {
      const event = deferred
      if (!event) return 'unavailable'
      deferred = null
      notify()
      try {
        await event.prompt()
      } catch {
        // Chromium refuses a prompt outside a user gesture or a second time; the event is spent either way.
        return 'unavailable'
      }
      const choice = await event.userChoice.catch(() => null)
      return choice?.outcome ?? 'dismissed'
    },
    dispose() {
      target?.removeEventListener('beforeinstallprompt', onBeforeInstallPrompt)
      target?.removeEventListener('appinstalled', onAppInstalled)
      listeners.clear()
    },
  }
}
