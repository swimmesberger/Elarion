// Compiled under the WebWorker library only (tsconfig.sw-test.json): the real service-worker global must satisfy
// the structural scope the handlers are typed against.
import { registerWebPushHandlers } from '../src/sw.js'

declare const worker: ServiceWorkerGlobalScope

registerWebPushHandlers(worker, { icon: '/icon.png', notificationOptions: () => ({ requireInteraction: true }) })
