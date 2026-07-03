// The Invoicing module manifest: its own sidebar entry plus a cross-module contribution — a "new invoice"
// action on the Clients table, wired by importing the Clients module's published token (its frontend
// [ModuleContract]). The lazy `component` keeps the dialog out of the initial bundle and out of Clients'
// chunk: contributing costs the token import, nothing more.
import { createRoute, lazyRouteComponent } from "@tanstack/react-router"
import { FilePlus2, ReceiptText } from "lucide-react"
import { lazy } from "react"
import { Modules, Permissions } from "@/generated/session-client"
import { contribute, defineModule } from "@/platform/contributions"
import { sidebarItems } from "@/platform/points"
import { redirectUnless, rootRoute } from "@/platform/router"
import { clientRowActions } from "@/modules/clients"

export const invoicingManifest = defineModule({
  name: Modules.Invoicing,
  when: { module: Modules.Invoicing },
  contributes: [
    contribute(sidebarItems, [
      {
        id: "invoices",
        label: "Invoices",
        icon: ReceiptText,
        to: "/invoices",
        order: 20,
        when: { permission: Permissions.invoices.read },
      },
    ]),
    contribute(clientRowActions, [
      {
        id: "create-invoice",
        label: "New invoice for this client",
        icon: FilePlus2,
        component: lazy(() =>
          import("./components/CreateInvoiceForClient").then((m) => ({
            default: m.CreateInvoiceForClient,
          }))
        ),
        when: { permission: Permissions.invoices.write },
      },
    ]),
  ],
})

export const invoicingRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/invoices",
  // The route-level mirror of the sidebar item's `when` — the same clause shape, evaluated with the same
  // semantics — so a deep link into a hidden module bounces home. UX only: the handlers behind this page
  // still enforce [RequirePermission] on every call.
  beforeLoad: redirectUnless(
    { module: Modules.Invoicing, permission: Permissions.invoices.read },
    "/"
  ),
  component: lazyRouteComponent(() => import("./InvoicesPage"), "InvoicesPage"),
})
