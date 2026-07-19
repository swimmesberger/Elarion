// The Invoicing side of the cross-module row action: mounted by the Clients table (through the
// clientRowActions point) with the row's client already picked, so the dialog skips the client selector.
// This file loads lazily on first use — it lives in Invoicing's chunk, not in Clients'.
import {useState} from "react"
import {toast} from "sonner"
import {Button} from "@/components/ui/button"
import {Input} from "@/components/ui/input"
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import type {ContextOf} from "@swimmesberger/elarion-contributions"
import {clientRowActions} from "@/modules/clients"
import {useCreateInvoice} from "../hooks/useInvoices"

// ContextOf single-sources the props from the point's declaration: if Clients evolves what its slot
// supplies, this component's signature follows — the payload and the slot site can't drift.
export function CreateInvoiceForClient({client, onClose}: ContextOf<typeof clientRowActions>) {
  const [amount, setAmount] = useState("19.99")
  const [currency, setCurrency] = useState("EUR")
  const [dueDate, setDueDate] = useState(() => new Date().toISOString().slice(0, 10))
  const createInvoice = useCreateInvoice()

  function submit() {
    createInvoice.mutate(
      {
        clientId: client.id,
        amountCents: Math.round(Number(amount) * 100),
        currency,
        dueDate,
      },
      {
        onSuccess: (res) => {
          // The email send continues in the background; the invoices page polls its job status.
          toast.success(`Created invoice ${res.number} for ${client.name} — sending…`)
          onClose()
        },
        onError: (err) => toast.error(err.message),
      }
    )
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>New invoice — {client.name}</DialogTitle>
        </DialogHeader>
        <div className="flex gap-2">
          <Input
            type="number"
            step="0.01"
            placeholder="Amount"
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
          />
          <Input
            placeholder="Currency"
            value={currency}
            maxLength={3}
            onChange={(e) => setCurrency(e.target.value.toUpperCase())}
          />
        </div>
        <Input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)}/>
        <DialogFooter>
          <Button onClick={submit} disabled={createInvoice.isPending}>
            {createInvoice.isPending ? "Creating…" : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
