# @swimmesberger/elarion-webpush

The browser half of [Elarion Web Push](https://elarion.wimmesberger.dev/docs/capabilities/web-push): reach a
user's browser or installed PWA while the app is closed. The server half is the `Elarion.WebPush` NuGet
package (`IWebPushSender`, VAPID keys, the subscription store).

- `pushAvailability()` → `'available' | 'install-first' | 'unsupported'`. `install-first` is iOS/iPadOS Safari
  outside a Home Screen app, where `PushManager` does not exist yet: show "Add to Home Screen first" instead of
  a switch that can never turn on.
- `enablePush(api)` — asks for permission **inside the click handler** (before any `await`, which Safari
  requires) and subscribes.
- `subscribe`, `unsubscribe`, `isSubscribed`, and `refreshOnStart(api)` — call the last one on every app start
  to heal subscriptions the push service rotated or the server cleaned up.
- `@swimmesberger/elarion-webpush/sw` — `registerWebPushHandlers(self)` for the service worker: shows the
  notification, focuses or opens the app at its URL on click, and re-subscribes on `pushsubscriptionchange`.

## Page

```ts
import { enablePush, fetchWebPushApi, pushAvailability, refreshOnStart } from '@swimmesberger/elarion-webpush'

const api = fetchWebPushApi() // the endpoints app.MapElarionWebPush() maps under /webpush

await navigator.serviceWorker.register('/sw.js', { type: 'module' })
void refreshOnStart(api)

switch (pushAvailability()) {
  case 'available':
    button.onclick = async () => console.log(await enablePush(api)) // 'subscribed' | 'denied' | 'dismissed'
    break
  case 'install-first':
    hint.textContent = 'Add this app to your Home Screen to turn on notifications.'
    break
}
```

When the application exposes its own `webPush.*` handlers instead of `MapElarionWebPush`, adapt the generated
JSON-RPC client:

```ts
const api: WebPushServerApi = {
  getPublicKey: async () => (await rpc.webPush.publicKey({})).publicKey,
  subscribe: async (subscription) => { await rpc.webPush.subscribe(subscription) },
  unsubscribe: async (endpoint) => { await rpc.webPush.unsubscribe({ endpoint }) },
}
```

## Service worker

```ts
import { registerWebPushHandlers } from '@swimmesberger/elarion-webpush/sw'

registerWebPushHandlers(self, { icon: '/icons/192.png', badge: '/icons/badge.png' })
```

The module is ESM: bundle the worker (e.g. vite-plugin-pwa `injectManifest`) or register it with
`{ type: 'module' }`.
