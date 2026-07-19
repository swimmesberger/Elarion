// The Clients module manifest and route subtree. Everything this module adds to the app — its sidebar
// entry, its route, its published extension point — is declared here or exported from index.ts; no shell
// file knows this module exists.
import {createRoute, lazyRouteComponent} from "@tanstack/react-router"
import {Users} from "lucide-react"
import {Modules, Permissions} from "@/generated/session-client"
import {contribute, defineModule} from "@/platform/contributions"
import {sidebarItems} from "@/platform/points"
import {rootRoute} from "@/platform/router"

export const clientsManifest = defineModule({
  name: Modules.Clients,
  // Backend-paired: one module-level gate removes every contribution when the backend disables the module.
  when: {module: Modules.Clients},
  contributes: [
    contribute(sidebarItems, [
      {
        id: "clients",
        label: "Clients",
        icon: Users,
        to: "/clients",
        order: 10,
        when: {permission: Permissions.clients.read},
      },
    ]),
  ],
})

// A lazy page component keeps the module's UI in its own chunk — it downloads on first navigation.
export const clientsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/clients",
  component: lazyRouteComponent(() => import("./ClientsPage"), "ClientsPage"),
})
