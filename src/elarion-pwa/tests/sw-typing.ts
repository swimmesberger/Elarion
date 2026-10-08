// Compiled under the WebWorker library only (tsconfig.sw-test.json): the real service-worker global must satisfy the
// structural scope the router is typed against — next to the Web Push handlers, as one worker composes both.
import { registerWebPushHandlers } from '../../elarion-webpush/src/sw.js'
import { ELARION_SERVER_PREFIXES, registerShellRouter } from '../src/sw.js'

declare const worker: ServiceWorkerGlobalScope

const router = registerShellRouter(worker, {
  cacheName: 'app-v1',
  serverPrefixes: [...ELARION_SERVER_PREFIXES, '/auth'],
  navigation: { mode: 'network-only', offlineUrl: '/offline.html' },
  staticFiles: ['/manifest.webmanifest'],
})
registerWebPushHandlers(worker, { icon: '/icon-192.png' })

worker.addEventListener('fetch', (event) => {
  if (new URL(event.request.url).pathname.startsWith('/fonts/')) {
    event.respondWith(router.strategies.staleWhileRevalidate(event))
  }
})
