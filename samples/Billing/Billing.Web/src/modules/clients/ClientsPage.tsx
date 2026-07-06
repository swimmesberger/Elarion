import { ClientsTable } from "./components/ClientsTable"
import { CreateClientDialog } from "./components/CreateClientDialog"

export function ClientsPage() {
  return (
    <section className="max-w-3xl">
      <div className="mb-3 flex items-center justify-between">
        <h2 className="text-lg font-medium">Clients</h2>
        <CreateClientDialog />
      </div>
      <ClientsTable />
    </section>
  )
}
