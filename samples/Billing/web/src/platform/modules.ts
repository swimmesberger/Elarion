// What a frontend module's public entry (modules/{name}/index.ts) default-exports: its contribution
// manifest plus the routes it owns. Manifests are DISCOVERED (import.meta.glob in app.tsx — a new
// contribution or a route-less module needs no central edit); routes are REGISTERED (one typed line per
// module in app.tsx, the grain of a backend host's ProjectReference) so TanStack's Link/loader/param
// inference stays intact. Routes compose through TanStack Router's own API — Elarion adds no routing
// machinery (an ADR-0032 non-goal).
import type { AnyRoute } from "@tanstack/react-router"
import type { AppManifest } from "@/platform/contributions"

export interface AppModule {
  readonly manifest: AppManifest
  /** The route subtrees this module owns; empty for a UI-only module that only contributes to slots. */
  readonly routes: readonly AnyRoute[]
}
