import { CreateInvoiceDialog } from "./components/CreateInvoiceDialog"
import { InvoicesTable } from "./components/InvoicesTable"

export function InvoicesPage() {
  return (
    <section className="max-w-3xl">
      <div className="mb-3 flex items-center justify-between">
        <h2 className="text-lg font-medium">Invoices</h2>
        <CreateInvoiceDialog />
      </div>
      <InvoicesTable />
    </section>
  )
}
