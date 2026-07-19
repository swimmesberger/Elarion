import {Suspense, useState} from "react"
import {Button} from "@/components/ui/button"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import {ExtensionSlot, useContributions} from "@swimmesberger/elarion-contributions/react"
import {useClients} from "../hooks/useClients"
import {clientRowActions, type ClientRow} from "../points"

export function ClientsTable() {
  const {data, isPending, isError} = useClients()
  // This module renders its row-action slot without knowing who contributes: Invoicing adds "new invoice"
  // through the published clientRowActions point, and a module that is disabled contributes nothing.
  const actions = useContributions(clientRowActions)
  const [active, setActive] = useState<{ actionId: string; client: ClientRow } | null>(null)
  const activeAction = active === null ? undefined : actions.find((a) => a.id === active.actionId)

  if (isPending) return <p className="text-[var(--color-muted-foreground)]">Loading clients…</p>
  if (isError) return <p className="text-[var(--color-destructive)]">Failed to load clients.</p>

  if (data.clients.length === 0) {
    return <p className="text-[var(--color-muted-foreground)]">No clients yet.</p>
  }

  return (
    <>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Number</TableHead>
            <TableHead>Name</TableHead>
            <TableHead>Email</TableHead>
            {actions.length > 0 && <TableHead className="w-0"/>}
          </TableRow>
        </TableHeader>
        <TableBody>
          {data.clients.map((c) => (
            <TableRow key={c.id}>
              <TableCell className="font-mono">{c.number}</TableCell>
              <TableCell>{c.name}</TableCell>
              <TableCell>{c.email}</TableCell>
              {actions.length > 0 && (
                <TableCell className="text-right">
                  <div className="flex justify-end gap-1">
                    <ExtensionSlot
                      point={clientRowActions}
                      render={(action) => (
                        <Button
                          variant="ghost"
                          size="sm"
                          title={action.label}
                          onClick={() => setActive({actionId: action.id, client: c})}
                        >
                          <action.icon className="h-4 w-4"/>
                          <span className="sr-only">{action.label}</span>
                        </Button>
                      )}
                    />
                  </div>
                </TableCell>
              )}
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {/* The invoked action's component mounts lazily — a contributed dialog's chunk downloads on first use. */}
      {activeAction !== undefined && active !== null && (
        <Suspense>
          <activeAction.component client={active.client} onClose={() => setActive(null)}/>
        </Suspense>
      )}
    </>
  )
}
