// The Clients module's published extension point. Exporting the token from the module's public entry
// (index.ts) is the frontend [ModuleContract]: other modules import the token and contribute row actions
// without ever reaching into this module's components — and without this module knowing they exist.
import type { LucideIcon } from "lucide-react"
import type { ComponentType } from "react"
import type { RpcMethods } from "@/generated/rpc-types"
import { defineExtensionPoint } from "@/platform/contributions"

/** One row of the clients table, as the generated client returns it. */
export type ClientRow = RpcMethods["clients.list"]["result"]["clients"][number]

/** What the slot supplies to an invoked action: the row's client plus a close callback. */
export interface ClientRowActionProps {
  readonly client: ClientRow
  readonly onClose: () => void
}

/**
 * A row action on the clients table: label and icon render inline in the row; `component` mounts when the
 * action is invoked. Contribute it lazily (`lazy(() => import(...))`) so the action's UI stays in the
 * contributing module's own chunk.
 */
export interface ClientRowAction {
  readonly label: string
  readonly icon: LucideIcon
  readonly component: ComponentType<ClientRowActionProps>
}

export const clientRowActions = defineExtensionPoint<ClientRowAction, ClientRowActionProps>(
  "clients.rowActions"
)
