// The landing page makes the capability snapshot (ADR-0030) visible: which modules this deployment
// enables and what the current user may do — the same facts that gate every sidebar item, row action, and
// route in this app.
import { Modules } from "@/generated/session-client"
import { rootRoute, type RouterContext } from "@/platform/router"

export function HomePage() {
  // The glob-composed route tree erases per-route types from the global Register (the documented
  // auto-discovery tradeoff), so the context is annotated with the root context type explicitly.
  const { caps }: RouterContext = rootRoute.useRouteContext()
  const modules = Object.values(Modules).map((name) => ({ name, enabled: caps.isModuleEnabled(name) }))

  return (
    <section className="max-w-3xl">
      <header className="mb-8">
        <h2 className="text-2xl font-semibold">Welcome</h2>
        <p className="text-sm text-[var(--color-muted-foreground)]">
          Everything in the sidebar was contributed by a module and filtered by the capability snapshot
          below — one <code>GET /session</code> call, typed by the generated <code>session-client.ts</code>.
        </p>
      </header>

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="rounded-lg border border-[var(--color-border)] p-4">
          <h3 className="mb-2 text-sm font-medium">User</h3>
          {caps.user.isAuthenticated ? (
            <>
              <p className="font-mono text-sm">{caps.user.id}</p>
              <p className="mt-2 text-xs text-[var(--color-muted-foreground)]">
                {caps.user.permissions.length} permissions
                {caps.user.roles.length > 0 ? ` · roles: ${caps.user.roles.join(", ")}` : ""}
              </p>
            </>
          ) : (
            <p className="text-sm text-[var(--color-muted-foreground)]">
              Not authenticated — gated contributions are hidden.
            </p>
          )}
        </div>

        <div className="rounded-lg border border-[var(--color-border)] p-4">
          <h3 className="mb-2 text-sm font-medium">Modules</h3>
          <ul className="space-y-1">
            {modules.map((module) => (
              <li key={module.name} className="flex items-center gap-2 text-sm">
                <span
                  className={
                    module.enabled
                      ? "h-2 w-2 rounded-full bg-emerald-500"
                      : "h-2 w-2 rounded-full bg-[var(--color-border)]"
                  }
                />
                {module.name}
                {!module.enabled && (
                  <span className="text-xs text-[var(--color-muted-foreground)]">disabled</span>
                )}
              </li>
            ))}
          </ul>
        </div>

        <div className="rounded-lg border border-[var(--color-border)] p-4 sm:col-span-2">
          <h3 className="mb-2 text-sm font-medium">Permissions</h3>
          {caps.user.permissions.length > 0 ? (
            <div className="flex flex-wrap gap-1.5">
              {caps.user.permissions.map((permission) => (
                <code
                  key={permission}
                  className="rounded bg-[var(--color-muted)] px-1.5 py-0.5 text-xs"
                >
                  {permission}
                </code>
              ))}
            </div>
          ) : (
            <p className="text-sm text-[var(--color-muted-foreground)]">None.</p>
          )}
        </div>
      </div>
    </section>
  )
}
