// The application shell. The sidebar is not hard-coded: it renders whatever the enabled modules
// contributed to the `sidebarItems` point, so adding a module never edits this file — the
// review-isolation test of ADR-0032, extended to the frontend.
import {Link, Outlet} from "@tanstack/react-router"
import {useContributions} from "@swimmesberger/elarion-contributions/react"
import {sidebarItems} from "@/platform/points"

export function AppShell() {
  const items = useContributions(sidebarItems)
  return (
    <div className="flex min-h-screen">
      <aside className="w-56 shrink-0 border-r border-[var(--color-border)] px-3 py-6">
        <div className="mb-6 px-3">
          <Link to="/">
            <h1 className="text-lg font-semibold">Billing</h1>
          </Link>
          <p className="text-xs text-[var(--color-muted-foreground)]">Elarion sample</p>
        </div>
        <nav className="flex flex-col gap-1">
          {items.map((item) => (
            <Link
              key={item.id}
              to={item.to}
              className="flex items-center gap-2 rounded-md px-3 py-2 text-sm text-[var(--color-muted-foreground)] hover:bg-[var(--color-accent)] hover:text-[var(--color-foreground)]"
              activeProps={{
                className: "bg-[var(--color-accent)] font-medium text-[var(--color-foreground)]",
              }}
            >
              <item.icon className="h-4 w-4"/>
              {item.label}
            </Link>
          ))}
        </nav>
      </aside>
      <main className="flex-1 px-8 py-10">
        <Outlet/>
      </main>
    </div>
  )
}
