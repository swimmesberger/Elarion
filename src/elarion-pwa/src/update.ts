// Reload into a new deploy. An installed app is not reopened, it is resumed: it can stay in memory for days, keep
// talking to an API that was redeployed under it, and fail on a contract it does not know. So when the app returns
// to the foreground it asks the server which web build is live, and when that differs from its own it reloads into
// it — on the next page change, never on the foreground return itself.
//
// Why not right away: coming back from the camera (a `capture` file input), a share sheet or a password manager is
// a foreground return too, and the page may hold a whole form in local state that a reload would wipe — with nothing
// on screen that looks busy, and iOS never asks on `beforeunload`. A page change is the safe moment: the user has
// left the page, and a page with unsaved input blocks its own navigation first. Even then an open dialog or a
// focused field postpones it, and a loop guard keeps a proxy that serves a stale version from turning every page
// change into a reload.
//
// The decisions are pure functions; createUpdateWatcher wires them to the document, the version source, and the
// application's router (navigationResolved / navigatedTo).

/** Where `appVersionFile()` from `@swimmesberger/elarion-pwa/vite` publishes the build version by default. */
export const DEFAULT_VERSION_URL = '/app-version.json'

/** No second check within this window: switching apps every few seconds must not mean a request each time. */
export const DEFAULT_CHECK_INTERVAL_MS = 60_000

/** No second update reload within this window — a proxy serving a stale version file must not cause a loop. */
export const DEFAULT_RELOAD_GUARD_MS = 60_000

/** The sessionStorage key of the last update reload (per tab; it survives the reload itself). */
export const DEFAULT_RELOAD_STORAGE_KEY = 'elarion.pwa.reloadedAt'

/** What counts as "the user is in the middle of something" besides a focused field: an open dialog of any kind. */
export const BUSY_SELECTOR = '[role="dialog"], [role="alertdialog"], dialog[open]'

/**
 * What a check found: `stay` (same build, or no answer) or `pending` — a different build is live and the app
 * reloads into it on the next safe page change ({@link shouldReloadOnNavigation}). There is no "reload now".
 */
export type UpdateDecision = 'stay' | 'pending'

/** Asks the server which build it ships now; `null` when it cannot tell (offline, mid-deploy, malformed). */
export type VersionSource = () => Promise<string | null>

/** Whether a foreground return should ask the server again. */
export function shouldCheckForUpdate({
  now,
  lastCheckAt,
  intervalMs = DEFAULT_CHECK_INTERVAL_MS,
}: {
  now: number
  lastCheckAt: number
  intervalMs?: number
}): boolean {
  return now - lastCheckAt >= intervalMs
}

/** Compares the running build with the served one. No answer is never a reason to reload. */
export function updateDecision({
  currentVersion,
  servedVersion,
}: {
  currentVersion: string
  servedVersion: string | null
}): UpdateDecision {
  return servedVersion === null || servedVersion === currentVersion ? 'stay' : 'pending'
}

/**
 * Whether a resolved navigation applies a pending update. Only a navigation that changed the **path** counts: a
 * search box that writes `?q=` on every keystroke and a router re-resolving the same location must not reload
 * under the user's input. Not while the page is busy (an open dialog, a focused field), and not within
 * `guardMs` of the last update reload.
 */
export function shouldReloadOnNavigation({
  pending,
  pathChanged,
  busy,
  now,
  lastReloadAt,
  guardMs = DEFAULT_RELOAD_GUARD_MS,
}: {
  pending: boolean
  pathChanged: boolean
  busy: boolean
  now: number
  lastReloadAt: number
  guardMs?: number
}): boolean {
  return pending && pathChanged && !busy && now - lastReloadAt >= guardMs
}

/** The `version` out of a version file's JSON body, or `null` for anything that is not a non-empty string there. */
export function parseServedVersion(body: unknown): string | null {
  if (typeof body !== 'object' || body === null) return null
  const version = (body as { version?: unknown }).version
  return typeof version === 'string' && version.trim() !== '' ? version.trim() : null
}

/** The first module entry script under `/assets/` in an HTML document — a content-hashed name, so a build id. */
export function parseEntryScript(html: string): string | null {
  for (const match of html.matchAll(/<script\b[^>]*>/gi)) {
    const tag = match[0]
    if (!/\btype\s*=\s*["']?module\b/i.test(tag)) continue
    const src = /\bsrc\s*=\s*["']([^"']+)["']/i.exec(tag)?.[1]
    if (src?.startsWith('/assets/')) return src
  }
  return null
}

/** The element shape {@link isTextEntry} reads — structural, so it works on `document.activeElement`. */
export interface FocusTarget {
  readonly tagName: string
  readonly isContentEditable?: boolean
  readonly type?: string
}

/** `<input>` types that never raise a keyboard or hold typed text. */
const NON_TEXT_INPUT_TYPES = new Set([
  'button',
  'checkbox',
  'color',
  'file',
  'hidden',
  'image',
  'radio',
  'range',
  'reset',
  'submit',
])

/** Whether focus on this element means the user is typing (a text input, textarea, select, or contenteditable). */
export function isTextEntry(target: FocusTarget | null | undefined): boolean {
  if (!target) return false
  if (target.isContentEditable) return true
  const tag = target.tagName.toUpperCase()
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true
  if (tag !== 'INPUT') return false
  return !NON_TEXT_INPUT_TYPES.has((target.type || 'text').toLowerCase())
}

/** The document shape {@link isDocumentBusy} reads. */
export interface BusyDocument {
  querySelector(selector: string): unknown
  readonly activeElement: FocusTarget | null
}

/** The default busy predicate: an open dialog ({@link BUSY_SELECTOR}) or a focused text field. */
export function isDocumentBusy(doc: BusyDocument | undefined = globalThis.document): boolean {
  if (!doc) return false
  return doc.querySelector(BUSY_SELECTOR) !== null || isTextEntry(doc.activeElement)
}

/**
 * Reads the build version from a JSON file such as the one `appVersionFile()` emits (`{"version":"…"}`). The
 * request is `cache: 'no-store'`, which skips the HTTP cache and which the `/sw` shell router leaves to the network.
 */
export function jsonVersionSource(url: string = DEFAULT_VERSION_URL): VersionSource {
  return async () => {
    try {
      const response = await fetch(url, { cache: 'no-store', credentials: 'same-origin' })
      if (!response.ok) return null
      return parseServedVersion(await response.json())
    } catch {
      // Offline, or the server is restarting mid-deploy: ask again on the next foreground return.
      return null
    }
  }
}

/**
 * Reads the build version from the served HTML shell: its hashed entry script ({@link parseEntryScript}). Needs no
 * build step — pair it with {@link currentEntryScript} as the running version — but costs a shell render per
 * check, so prefer {@link jsonVersionSource} where the build can publish a version file.
 */
export function entryScriptVersionSource(url = '/'): VersionSource {
  return async () => {
    try {
      const response = await fetch(url, {
        cache: 'no-store',
        credentials: 'same-origin',
        headers: { accept: 'text/html' },
      })
      if (!response.ok) return null
      return parseEntryScript(await response.text())
    } catch {
      return null
    }
  }
}

/** The running document's hashed entry script, or `null` (e.g. under a dev server, which serves no hashed entry). */
export function currentEntryScript(
  doc: Pick<Document, 'querySelector'> | undefined = globalThis.document,
): string | null {
  const script = doc?.querySelector<HTMLScriptElement>('script[type="module"][src^="/assets/"]')
  return script?.getAttribute('src') ?? null
}

/** The visibility surface the watcher listens on. */
export interface VisibilityDocument {
  readonly visibilityState: DocumentVisibilityState
  addEventListener(type: 'visibilitychange', listener: () => void): void
  removeEventListener(type: 'visibilitychange', listener: () => void): void
}

/** The storage the reload guard lives in. */
export interface ReloadGuardStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

/** Options for {@link createUpdateWatcher}. */
export interface UpdateWatcherOptions {
  /**
   * The running build's version — the same value the server publishes (bake it into the bundle, e.g. with
   * `appVersionFile({ define })`). An empty or missing value disables the watcher.
   */
  currentVersion: string | null | undefined
  /** Asks the server which build is live. Defaults to `jsonVersionSource('/app-version.json')`. */
  servedVersion?: VersionSource
  /** Set `false` to disable checking entirely — e.g. `import.meta.env.PROD`, so a dev server never checks. */
  enabled?: boolean
  /** Whether the user is in the middle of something right now. Defaults to {@link isDocumentBusy}. */
  isBusy?: () => boolean
  /** Minimum time between two foreground checks. Defaults to 60 s. */
  checkIntervalMs?: number
  /** Minimum time between two update reloads of this tab. Defaults to 60 s. */
  reloadGuardMs?: number
  /** The sessionStorage key of the reload guard. Defaults to {@link DEFAULT_RELOAD_STORAGE_KEY}. */
  storageKey?: string
  /** Where the reload guard is kept. Defaults to `sessionStorage`; `null` keeps it in memory only. */
  storage?: ReloadGuardStorage | null
  /** The document whose foreground returns trigger a check. Defaults to `document`. */
  document?: VisibilityDocument
  /** The path the page started on, the baseline of {@link UpdateWatcher.navigatedTo}. Defaults to `location.pathname`. */
  initialPath?: string
  /** Reloads the page. Defaults to `location.reload()`. */
  reload?: () => void
  /** The clock. Defaults to `Date.now`. */
  now?: () => number
}

/** Watches for a new deploy; see {@link createUpdateWatcher}. */
export interface UpdateWatcher {
  /**
   * Asks the server now. Honors the check interval unless `force` is set — force it when a response failed this
   * build's validation, the surest sign a newer contract is live. A `pending` result still only reloads on the
   * next safe page change.
   */
  check(options?: { force?: boolean }): Promise<UpdateDecision>
  /** Whether a newer build is live and waiting for the next safe page change. */
  isPending(): boolean
  /** Calls `listener` when {@link isPending} changes — e.g. to show an "Update available" hint. */
  subscribe(listener: () => void): () => void
  /**
   * Call after every resolved navigation, with whether its **path** changed (TanStack Router's `onResolved` event
   * carries `pathChanged`). Reloads into a pending update when it is safe; returns whether it did.
   */
  navigationResolved(change: { pathChanged: boolean }): boolean
  /** Like {@link navigationResolved}, for routers that only report the new pathname; the watcher compares paths. */
  navigatedTo(pathname: string): boolean
  /** Applies a pending update at once — for an explicit "Reload" action. Returns whether it reloaded. */
  reloadNow(): boolean
  /** Stops listening for foreground returns. */
  dispose(): void
}

/**
 * Watches for a new deploy of the web build and reloads into it at a safe moment: checks when the app returns to the
 * foreground (at most once per `checkIntervalMs`), marks a different served version as pending, and reloads on the
 * next navigation that changed the path while no dialog is open and no field is focused — never more than once per
 * `reloadGuardMs`.
 *
 * @example
 * ```ts
 * const updates = createUpdateWatcher({ currentVersion: __APP_VERSION__, enabled: import.meta.env.PROD })
 * router.subscribe('onResolved', (event) => updates.navigationResolved({ pathChanged: event.pathChanged }))
 * ```
 */
export function createUpdateWatcher(options: UpdateWatcherOptions): UpdateWatcher {
  const currentVersion = options.currentVersion?.trim() ?? ''
  const enabled = options.enabled !== false && currentVersion !== ''
  const servedVersion = options.servedVersion ?? jsonVersionSource()
  const isBusy = options.isBusy ?? (() => isDocumentBusy())
  const checkIntervalMs = options.checkIntervalMs ?? DEFAULT_CHECK_INTERVAL_MS
  const reloadGuardMs = options.reloadGuardMs ?? DEFAULT_RELOAD_GUARD_MS
  const storageKey = options.storageKey ?? DEFAULT_RELOAD_STORAGE_KEY
  const storage = options.storage === undefined ? sessionStorageOrNull() : options.storage
  const doc = options.document ?? (globalThis.document as VisibilityDocument | undefined)
  const reload = options.reload ?? (() => globalThis.location.reload())
  const now = options.now ?? Date.now
  const listeners = new Set<() => void>()

  let pending = false
  let lastCheckAt = Number.NEGATIVE_INFINITY
  let inFlight: Promise<UpdateDecision> | null = null
  let lastPath = options.initialPath ?? globalThis.location?.pathname ?? ''
  let memoryReloadAt = Number.NEGATIVE_INFINITY

  const setPending = (value: boolean) => {
    if (pending === value) return
    pending = value
    for (const listener of [...listeners]) listener()
  }

  const readLastReloadAt = (): number => {
    try {
      const stored = Number(storage?.getItem(storageKey) ?? Number.NaN)
      return Number.isFinite(stored) ? Math.max(stored, memoryReloadAt) : memoryReloadAt
    } catch {
      return memoryReloadAt
    }
  }

  const doReload = (at: number) => {
    memoryReloadAt = at
    try {
      storage?.setItem(storageKey, String(at))
    } catch {
      // Private mode or storage disabled: the guard is best effort, the check interval still throttles.
    }
    setPending(false)
    reload()
  }

  async function check({ force = false }: { force?: boolean } = {}): Promise<UpdateDecision> {
    if (!enabled) return 'stay'
    if (inFlight) return inFlight
    const at = now()
    if (!force && !shouldCheckForUpdate({ now: at, lastCheckAt, intervalMs: checkIntervalMs })) {
      return pending ? 'pending' : 'stay'
    }
    lastCheckAt = at
    inFlight = (async () => {
      const served = await servedVersion().catch(() => null)
      // No answer keeps what an earlier check found; an answer equal to ours clears it (a rolled-back deploy).
      if (served !== null) setPending(updateDecision({ currentVersion, servedVersion: served }) === 'pending')
      return pending ? 'pending' : 'stay'
    })()
    try {
      return await inFlight
    } finally {
      inFlight = null
    }
  }

  const onVisibilityChange = () => {
    if (doc?.visibilityState === 'visible') void check()
  }
  if (enabled) doc?.addEventListener('visibilitychange', onVisibilityChange)

  function navigationResolved({ pathChanged }: { pathChanged: boolean }): boolean {
    if (!pending) return false
    const at = now()
    const reloadNow = shouldReloadOnNavigation({
      pending,
      pathChanged,
      busy: isBusy(),
      now: at,
      lastReloadAt: readLastReloadAt(),
      guardMs: reloadGuardMs,
    })
    if (reloadNow) doReload(at)
    return reloadNow
  }

  return {
    check,
    isPending: () => pending,
    subscribe(listener) {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    navigationResolved,
    navigatedTo(pathname) {
      const pathChanged = pathname !== lastPath
      lastPath = pathname
      return navigationResolved({ pathChanged })
    },
    reloadNow() {
      if (!pending) return false
      doReload(now())
      return true
    },
    dispose() {
      doc?.removeEventListener('visibilitychange', onVisibilityChange)
      listeners.clear()
    },
  }
}

function sessionStorageOrNull(): ReloadGuardStorage | null {
  try {
    return globalThis.sessionStorage ?? null
  } catch {
    // Reading sessionStorage throws where storage is blocked (sandboxed frames, some privacy modes).
    return null
  }
}
