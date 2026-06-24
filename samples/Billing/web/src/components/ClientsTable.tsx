import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { useClients } from "@/hooks/useClients"

export function ClientsTable() {
  const { data, isPending, isError } = useClients()

  if (isPending) return <p className="text-[var(--color-muted-foreground)]">Loading clients…</p>
  if (isError) return <p className="text-[var(--color-destructive)]">Failed to load clients.</p>

  if (data.clients.length === 0) {
    return <p className="text-[var(--color-muted-foreground)]">No clients yet.</p>
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Number</TableHead>
          <TableHead>Name</TableHead>
          <TableHead>Email</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {data.clients.map((c) => (
          <TableRow key={c.id}>
            <TableCell className="font-mono">{c.number}</TableCell>
            <TableCell>{c.name}</TableCell>
            <TableCell>{c.email}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}
