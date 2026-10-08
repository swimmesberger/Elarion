# @swimmesberger/elarion-pwa

The installable-app shell for Elarion frontends — what turns "Add to Home Screen" into an app that opens offline,
loads its assets from the cache, and moves to a new deploy without throwing away what the user was typing. Guide:
[Installable apps (PWA)](https://elarion.wimmesberger.dev/docs/capabilities/pwa).

- **Install** — `createInstallPrompt()` captures Chromium's `beforeinstallprompt` and reports `availability()`:
  `'prompt' | 'ios' | 'none'` (`ios` = show the "Share → Add to Home Screen" instructions). `isStandalone()`,
  `isAppleTouchDevice()` and the pure `installOption()` are exported too.
- **Updates** — `createUpdateWatcher({ currentVersion })` asks the server which web build is live when the app returns
  to the foreground and reloads into a new one **on the next navigation that changed the path**, never on the
  foreground return itself, never while a dialog is open or a field is focused, at most once a minute.
- **Registration** — `registerServiceWorker('/sw.js')` with `updateViaCache: 'none'`; resolves `null` instead of
  throwing where there is no service worker.
- **`/sw`** — `registerShellRouter(self, …)`: network-only or network-first navigations, cache-first hashed assets,
  stale-while-revalidate icons and manifest; never touches the API (`ELARION_SERVER_PREFIXES`), non-GET, cross-origin
  or `no-store` requests, and never stores a redirected, opaque, partial, `no-store`, or (outside the shell) HTML answer.
  Composes with `registerWebPushHandlers(self)` from `@swimmesberger/elarion-webpush/sw` in the same worker.
- **`/vite`** — `appVersionFile({ version, define })` emits `/app-version.json` and bakes the same version into the
  bundle.

## Page

```ts
import { createInstallPrompt, createUpdateWatcher, registerServiceWorker } from '@swimmesberger/elarion-pwa'

if (import.meta.env.PROD) void registerServiceWorker('/sw.js')

export const install = createInstallPrompt() // at module load: the prompt event fires early

export const updates = createUpdateWatcher({ currentVersion: __APP_VERSION__, enabled: import.meta.env.PROD })
router.subscribe('onResolved', (event) => updates.navigationResolved({ pathChanged: event.pathChanged }))
```

## Service worker

```ts
import { ELARION_SERVER_PREFIXES, registerShellRouter } from '@swimmesberger/elarion-pwa/sw'
import { registerWebPushHandlers } from '@swimmesberger/elarion-webpush/sw'

registerShellRouter(self, {
  cacheName: 'myapp-v1',
  serverPrefixes: [...ELARION_SERVER_PREFIXES, '/auth'],
  navigation: { mode: 'network-only', offlineUrl: '/offline.html' }, // or { mode: 'network-first' } for an SPA shell
  staticFiles: ['/manifest.webmanifest', '/icon-192.png', '/icon-512.png', '/apple-touch-icon.png'],
})
registerWebPushHandlers(self, { icon: '/icon-192.png' })
```

The module is ESM: bundle the worker into one classic script served at `/sw.js` (for example a second
`vite build` with `build.lib.formats: ['iife']`), or register it with `{ type: 'module' }`.

## Vite

```ts
import { appVersionFile } from '@swimmesberger/elarion-pwa/vite'

const version = process.env.APP_VERSION?.trim() || 'dev'
export default defineConfig({ plugins: [appVersionFile({ version, define: '__APP_VERSION__' })] })
```
