import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createUpdateWatcher,
  DEFAULT_CHECK_INTERVAL_MS,
  DEFAULT_RELOAD_GUARD_MS,
  isDocumentBusy,
  isTextEntry,
  jsonVersionSource,
  parseEntryScript,
  parseServedVersion,
  registerServiceWorker,
  shouldCheckForUpdate,
  shouldReloadOnNavigation,
  updateDecision,
  type ReloadGuardStorage,
  type UpdateWatcherOptions,
  type VisibilityDocument,
} from '../src/index.js'

// An installed app is resumed, not reopened: on returning to the foreground it asks which web build is live, and
// reloads into a new one on the next safe page change — never on the foreground return itself. Coming back from
// the camera is a foreground return, and a reload there wiped a whole form held in local state.

const NOW = 10_000_000

describe('decisions', () => {
  it('checks at most once per interval', () => {
    expect(shouldCheckForUpdate({ now: NOW, lastCheckAt: 0 })).toBe(true)
    expect(shouldCheckForUpdate({ now: NOW, lastCheckAt: NOW - DEFAULT_CHECK_INTERVAL_MS + 1 })).toBe(false)
    expect(shouldCheckForUpdate({ now: NOW, lastCheckAt: NOW - DEFAULT_CHECK_INTERVAL_MS })).toBe(true)
    expect(shouldCheckForUpdate({ now: NOW, lastCheckAt: NOW - 5, intervalMs: 5 })).toBe(true)
  })

  it('only ever marks a new build pending — no answer is never a reason', () => {
    expect(updateDecision({ currentVersion: 'v1', servedVersion: 'v1' })).toBe('stay')
    expect(updateDecision({ currentVersion: 'v1', servedVersion: null })).toBe('stay')
    expect(updateDecision({ currentVersion: 'v1', servedVersion: 'v2' })).toBe('pending')
  })

  const nav = { pending: true, pathChanged: true, busy: false, now: NOW, lastReloadAt: 0 }

  it('reloads a pending update on a navigation that changed the path', () => {
    expect(shouldReloadOnNavigation(nav)).toBe(true)
    expect(shouldReloadOnNavigation({ ...nav, pending: false })).toBe(false)
  })

  it('does not reload on a search-only or same-location navigation', () => {
    expect(shouldReloadOnNavigation({ ...nav, pathChanged: false })).toBe(false)
  })

  it('postpones while the page is busy', () => {
    expect(shouldReloadOnNavigation({ ...nav, busy: true })).toBe(false)
  })

  it('reloads at most once per guard window, so a stale proxy cannot loop', () => {
    expect(shouldReloadOnNavigation({ ...nav, lastReloadAt: NOW - DEFAULT_RELOAD_GUARD_MS + 1 })).toBe(false)
    expect(shouldReloadOnNavigation({ ...nav, lastReloadAt: NOW - DEFAULT_RELOAD_GUARD_MS })).toBe(true)
  })

  it('reads a version file and an entry script defensively', () => {
    expect(parseServedVersion({ version: ' 2026-10-07.12 ' })).toBe('2026-10-07.12')
    for (const body of [null, 'v1', {}, { version: '' }, { version: '  ' }, { version: 3 }]) {
      expect(parseServedVersion(body)).toBeNull()
    }
    expect(
      parseEntryScript('<script>boot()</script><script type="module" crossorigin src="/assets/index-B3xK9aZq.js"></script>'),
    ).toBe('/assets/index-B3xK9aZq.js')
    expect(parseEntryScript('<script type="module" src="/@vite/client"></script>')).toBeNull()
  })

  it('treats a focused text field or an open dialog as busy', () => {
    expect(isTextEntry({ tagName: 'input', type: 'text' })).toBe(true)
    expect(isTextEntry({ tagName: 'INPUT' })).toBe(true)
    expect(isTextEntry({ tagName: 'INPUT', type: 'checkbox' })).toBe(false)
    expect(isTextEntry({ tagName: 'TEXTAREA' })).toBe(true)
    expect(isTextEntry({ tagName: 'DIV', isContentEditable: true })).toBe(true)
    expect(isTextEntry({ tagName: 'BUTTON' })).toBe(false)
    expect(isTextEntry(null)).toBe(false)

    const doc = (dialog: boolean, activeElement: { tagName: string } | null) => ({
      querySelector: (selector: string) => (dialog && selector.includes('[role="dialog"]') ? {} : null),
      activeElement,
    })
    expect(isDocumentBusy(doc(true, null))).toBe(true)
    expect(isDocumentBusy(doc(false, { tagName: 'TEXTAREA' }))).toBe(true)
    expect(isDocumentBusy(doc(false, { tagName: 'BODY' }))).toBe(false)
    expect(isDocumentBusy(undefined)).toBe(false)
  })
})

class FakeDocument implements VisibilityDocument {
  visibilityState: DocumentVisibilityState = 'visible'
  private readonly listeners = new Set<() => void>()
  addEventListener(_type: 'visibilitychange', listener: () => void) {
    this.listeners.add(listener)
  }
  removeEventListener(_type: 'visibilitychange', listener: () => void) {
    this.listeners.delete(listener)
  }
  /** Leaves and comes back, as resuming the installed app does. */
  resume() {
    this.visibilityState = 'hidden'
    this.listeners.forEach((listener) => listener())
    this.visibilityState = 'visible'
    this.listeners.forEach((listener) => listener())
  }
  get listening() {
    return this.listeners.size
  }
}

function memoryStorage(): ReloadGuardStorage & { values: Map<string, string> } {
  const values = new Map<string, string>()
  return { values, getItem: (key) => values.get(key) ?? null, setItem: (key, value) => void values.set(key, value) }
}

function setup(overrides: Partial<UpdateWatcherOptions> = {}) {
  const clock = { now: NOW }
  const document = new FakeDocument()
  const storage = memoryStorage()
  const served = { version: 'v2' as string | null }
  const servedVersion = vi.fn(async () => served.version)
  const reload = vi.fn()
  const busy = { value: false }
  const watcher = createUpdateWatcher({
    currentVersion: 'v1',
    servedVersion,
    document,
    storage,
    reload,
    now: () => clock.now,
    isBusy: () => busy.value,
    initialPath: '/inspections/7',
    ...overrides,
  })
  // A foreground check runs asynchronously; let it settle.
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0))
  return { watcher, document, storage, served, servedVersion, reload, busy, clock, settle }
}

describe('createUpdateWatcher', () => {
  it('never reloads on the foreground return itself, only on the next path change', async () => {
    const { watcher, document, reload, settle } = setup()

    document.resume()
    await settle()

    expect(watcher.isPending()).toBe(true)
    expect(reload).not.toHaveBeenCalled()
    expect(watcher.navigationResolved({ pathChanged: false })).toBe(false)
    expect(watcher.navigationResolved({ pathChanged: true })).toBe(true)
    expect(reload).toHaveBeenCalledTimes(1)
    expect(watcher.isPending()).toBe(false)
  })

  it('compares paths itself for routers that only report the pathname', async () => {
    const { watcher, reload } = setup()
    await watcher.check()

    expect(watcher.navigatedTo('/inspections/7')).toBe(false)
    expect(watcher.navigatedTo('/inspections')).toBe(true)
    expect(reload).toHaveBeenCalledTimes(1)
  })

  it('postpones while a dialog is open or a field has focus', async () => {
    const { watcher, busy, reload } = setup()
    await watcher.check()
    busy.value = true

    expect(watcher.navigationResolved({ pathChanged: true })).toBe(false)
    busy.value = false
    expect(watcher.navigationResolved({ pathChanged: true })).toBe(true)
    expect(reload).toHaveBeenCalledTimes(1)
  })

  it('keeps the reload guard across the reload, so a stale version file cannot loop', async () => {
    const first = setup()
    await first.watcher.check()
    first.watcher.navigationResolved({ pathChanged: true })

    // The reloaded page: same tab storage, still served the "other" version by a stale proxy.
    const second = setup({ storage: first.storage })
    second.clock.now = NOW + 1_000
    await second.watcher.check()
    expect(second.watcher.navigationResolved({ pathChanged: true })).toBe(false)

    second.clock.now = NOW + DEFAULT_RELOAD_GUARD_MS
    expect(second.watcher.navigationResolved({ pathChanged: true })).toBe(true)
  })

  it('asks at most once per interval, unless forced', async () => {
    const { watcher, document, servedVersion, clock, settle } = setup()
    document.resume()
    document.resume()
    await settle()
    expect(servedVersion).toHaveBeenCalledTimes(1)

    await watcher.check({ force: true })
    expect(servedVersion).toHaveBeenCalledTimes(2)

    clock.now += DEFAULT_CHECK_INTERVAL_MS
    document.resume()
    await settle()
    expect(servedVersion).toHaveBeenCalledTimes(3)
  })

  it('keeps a pending update through a failed check and drops it when the deploy was rolled back', async () => {
    const { watcher, served } = setup()
    expect(await watcher.check({ force: true })).toBe('pending')

    served.version = null
    expect(await watcher.check({ force: true })).toBe('pending')

    served.version = 'v1'
    expect(await watcher.check({ force: true })).toBe('stay')
    expect(watcher.isPending()).toBe(false)
  })

  it('notifies subscribers when an update becomes pending', async () => {
    const { watcher } = setup()
    const listener = vi.fn()
    watcher.subscribe(listener)

    await watcher.check()

    expect(listener).toHaveBeenCalledTimes(1)
  })

  it('applies a pending update at once on an explicit reload', async () => {
    const { watcher, busy, reload } = setup()
    expect(watcher.reloadNow()).toBe(false)
    await watcher.check()
    busy.value = true

    expect(watcher.reloadNow()).toBe(true)
    expect(reload).toHaveBeenCalledTimes(1)
  })

  it('does nothing when disabled or without a version of its own', async () => {
    for (const overrides of [{ enabled: false }, { currentVersion: '' }, { currentVersion: undefined }]) {
      const { watcher, document, servedVersion } = setup(overrides)
      expect(document.listening).toBe(0)
      expect(await watcher.check({ force: true })).toBe('stay')
      expect(servedVersion).not.toHaveBeenCalled()
    }
  })

  it('stops listening on dispose', () => {
    const { watcher, document } = setup()
    expect(document.listening).toBe(1)
    watcher.dispose()
    expect(document.listening).toBe(0)
  })
})

describe('jsonVersionSource', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('asks past every cache and reads the version', async () => {
    const fetch = vi.fn(async () => new Response('{"version":"v2"}', { status: 200 }))
    vi.stubGlobal('fetch', fetch)

    expect(await jsonVersionSource()()).toBe('v2')
    expect(fetch).toHaveBeenCalledWith('/app-version.json', { cache: 'no-store', credentials: 'same-origin' })
  })

  it('answers null when it cannot tell', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 502 })))
    expect(await jsonVersionSource('/v.json')()).toBeNull()
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('Failed to fetch'))))
    expect(await jsonVersionSource()()).toBeNull()
    vi.stubGlobal('fetch', vi.fn(async () => new Response('<html>', { status: 200 })))
    expect(await jsonVersionSource()()).toBeNull()
  })
})

describe('registerServiceWorker', () => {
  it('registers past the HTTP cache by default', async () => {
    const registration = {} as ServiceWorkerRegistration
    const register = vi.fn(async () => registration)

    expect(await registerServiceWorker('/sw.js', { container: { register } })).toBe(registration)
    expect(register).toHaveBeenCalledWith('/sw.js', { scope: undefined, type: undefined, updateViaCache: 'none' })
  })

  it('resolves null when registration fails or service workers are unavailable', async () => {
    const onError = vi.fn()
    const failing = { register: vi.fn(async () => Promise.reject(new Error('SecurityError'))) }

    expect(await registerServiceWorker('/sw.js', { container: failing, onError })).toBeNull()
    expect(onError).toHaveBeenCalledTimes(1)
    expect(await registerServiceWorker()).toBeNull()
  })
})
