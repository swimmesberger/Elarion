// The Clients module's public entry — the only file other code may import from this module (the ELMOD002
// boundary, held by convention in this sample; workspace-package `exports` enforce it at scale). It
// publishes the module itself for the composition root, the row-actions extension point for contributors,
// and the clients query hook as the module's in-process API.
import type {AppModule} from "@/platform/modules"
import {clientsManifest, clientsRoute} from "./module"

export {
  clientRowActions,
  type ClientRow,
  type ClientRowAction,
  type ClientRowActionProps,
} from "./points"
export {useClients} from "./hooks/useClients"

// `satisfies` (not a type annotation) keeps the routes' concrete types, so the static registration in
// app.tsx preserves TanStack's Link/loader/param inference.
const clientsModule = {manifest: clientsManifest, routes: [clientsRoute]} satisfies AppModule
export default clientsModule
