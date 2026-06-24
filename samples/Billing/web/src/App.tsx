import { ClientsTable } from "@/components/ClientsTable"
import { CreateClientDialog } from "@/components/CreateClientDialog"
import { CreateInvoiceDialog } from "@/components/CreateInvoiceDialog"
import { InvoicesTable } from "@/components/InvoicesTable"

export default function App() {
  return (
    <div className="mx-auto max-w-3xl px-4 py-10">
      <header className="mb-8">
        <h1 className="text-2xl font-semibold">Billing</h1>
        <p className="text-sm text-[var(--color-muted-foreground)]">
          A typed React client over the Elarion JSON-RPC API — every call is generated from the
          backend's <code>rpc-schema.json</code>.
        </p>
      </header>

      <section className="mb-10">
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-lg font-medium">Clients</h2>
          <CreateClientDialog />
        </div>
        <ClientsTable />
      </section>

      <section>
        <div className="mb-3 flex items-center justify-between">
          <h2 className="text-lg font-medium">Invoices</h2>
          <CreateInvoiceDialog />
        </div>
        <InvoicesTable />
      </section>
    </div>
  )
}
