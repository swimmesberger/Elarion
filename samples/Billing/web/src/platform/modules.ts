// What a frontend module's public entry (modules/{name}/index.ts) default-exports: its contribution
// manifest plus its route subtree. The composition root discovers these via import.meta.glob, so a new
// module is a new folder — no central registration. The manifest rides the framework kernel; routes
// compose through TanStack Router's own API — Elarion adds no routing machinery (an ADR-0032 non-goal),
// the app just bundles the two side by side.
import type { AnyRoute } from "@tanstack/react-router"
import type { AppManifest } from "@/platform/contributions"

export interface AppModule {
  readonly manifest: AppManifest
  readonly route: AnyRoute
}
