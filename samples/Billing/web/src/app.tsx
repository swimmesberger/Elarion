// The composition root. Frontend modules are discovered from the filesystem — adding a module means
// creating a folder under modules/ with an index.ts default export; no central file changes (the
// frontend analog of the generated AddElarion aggregation). Vite expands the glob at build time into
// static imports, so discovery stays compile-time, bundled, and deterministic (keys come back sorted).
//
// The documented tradeoff: a glob-composed route tree is typed as AnyRoute[], so `Link to` loses its
// literal-union checking. A team that prefers fully-typed navigation lists modules statically instead
// (`rootRoute.addChildren([indexRoute, clients.route, invoicing.route])`) at one line per module.
import { createRoute, createRouter } from "@tanstack/react-router"
import { HomePage } from "@/platform/HomePage"
import type { AppModule } from "@/platform/modules"
import { rootRoute } from "@/platform/router"

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

const routeTree = rootRoute.addChildren([indexRoute, ...appModules.map((m) => m.route)])

export const router = createRouter({
  routeTree,
  // The capability snapshot is loaded asynchronously at boot and supplied by <RouterProvider context=…>.
  context: { caps: undefined! },
})

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router
  }
}
