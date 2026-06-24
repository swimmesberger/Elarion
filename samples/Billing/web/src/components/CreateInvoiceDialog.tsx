import { useEffect, useState } from "react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import { useClients } from "@/hooks/useClients"
import { useCreateInvoice, useSendStatus } from "@/hooks/useInvoices"

export function CreateInvoiceDialog() {
  const [open, setOpen] = useState(false)
  const [clientId, setClientId] = useState("")
  const [amount, setAmount] = useState("19.99")
  const [currency, setCurrency] = useState("EUR")
  const [dueDate, setDueDate] = useState(() => new Date().toISOString().slice(0, 10))
  const [sendJobId, setSendJobId] = useState<string>()

  const clients = useClients()
  const createInvoice = useCreateInvoice()

  // Poll the background send job and surface its progress as toasts.
  const sendStatus = useSendStatus(sendJobId)
  useEffect(() => {
    if (!sendStatus.data) return
    if (sendStatus.data.status === "Succeeded") toast.success("Invoice email sent")
    if (sendStatus.data.status === "Failed") toast.error("Invoice email failed")
  }, [sendStatus.data])

  function submit() {
    createInvoice.mutate(
      {
        clientId,
        amountCents: Math.round(Number(amount) * 100),
        currency,
        dueDate,
      },
      {
        onSuccess: (res) => {
          toast.success(`Created invoice ${res.number} — sending…`)
          setSendJobId(res.sendJobId)
          setOpen(false)
        },
        onError: (err) => toast.error(err.message),
      }
    )
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button disabled={clients.data?.clients.length === 0}>New invoice</Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>New invoice</DialogTitle>
        </DialogHeader>
        <select
          className="flex h-9 w-full rounded-md border border-[var(--color-input)] bg-transparent px-3 text-sm"
          value={clientId}
          onChange={(e) => setClientId(e.target.value)}
        >
          <option value="">Select a client…</option>
          {clients.data?.clients.map((c) => (
            <option key={c.id} value={c.id}>
              {c.number} — {c.name}
            </option>
          ))}
        </select>
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
        <Input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} />
        <DialogFooter>
          <Button onClick={submit} disabled={!clientId || createInvoice.isPending}>
            {createInvoice.isPending ? "Creating…" : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
