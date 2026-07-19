// The composition root. Manifests are DISCOVERED — the import.meta.glob below feeds every module's
// contributions to the registry (main.tsx), so a new contribution, sidebar item, or route-less module
// needs no edit here. Routes are REGISTERED — one typed line per route-owning module in addChildren, the
// same grain as a backend host adding a ProjectReference — because a glob-composed route tree types as
// AnyRoute[], which silently degrades `Link to`, `useLoaderData`, and `useParams` to untyped fallbacks
// app-wide. (A team that prefers zero-edit route discovery can compose appModules.flatMap((m) => m.routes)
// and register the router as AnyRouter instead — the tradeoff is documented in the frontend-modules
// concept doc.)
import {createRoute, createRouter} from "@tanstack/react-router"
import clients from "@/modules/clients"
import invoicing from "@/modules/invoicing"
import {HomePage} from "@/platform/HomePage"
import type {AppModule} from "@/platform/modules"
import {rootRoute} from "@/platform/router"

// Vite expands the glob at build time into static imports, so manifest discovery stays compile-time,
// bundled, and deterministic (keys come back sorted).
const discovered = import.meta.glob<AppModule>("./modules/*/index.ts", {
  eager: true,
  import: "default",
})
export const appModules: ReadonlyArray<AppModule> = Object.values(discovered)

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  component: HomePage,
})

const routeTree = rootRoute.addChildren([indexRoute, ...clients.routes, ...invoicing.routes])

export const router = createRouter({
  routeTree,
  // The capability snapshot is loaded asynchronously at boot and supplied by <RouterProvider context=…>.
  context: {caps: undefined!},
})

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router
  }
}
