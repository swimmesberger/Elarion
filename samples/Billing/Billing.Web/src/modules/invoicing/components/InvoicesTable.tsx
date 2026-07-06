import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { useInvoices } from "../hooks/useInvoices"

function formatMoney(cents: number, currency: string) {
  return new Intl.NumberFormat(undefined, { style: "currency", currency }).format(cents / 100)
}

export function InvoicesTable() {
  const { data, isPending, isError } = useInvoices()

  if (isPending) return <p className="text-[var(--color-muted-foreground)]">Loading invoices…</p>
  if (isError) return <p className="text-[var(--color-destructive)]">Failed to load invoices.</p>

  if (data.invoices.length === 0) {
    return <p className="text-[var(--color-muted-foreground)]">No invoices yet.</p>
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Number</TableHead>
          <TableHead>Amount</TableHead>
          <TableHead>Status</TableHead>
          <TableHead>Due</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {data.invoices.map((i) => (
          <TableRow key={i.id}>
            <TableCell className="font-mono">{i.number}</TableCell>
            <TableCell>{formatMoney(i.amountCents, i.currency)}</TableCell>
            <TableCell>{i.status}</TableCell>
            <TableCell>{i.dueDate}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}
