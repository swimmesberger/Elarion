// Angular bindings over the contribution kernel — the `/angular` adapter of ADR-0032, the sibling of the
// `/react` sub-export. Deliberately decorator- and template-free: it ships only the DI + reactivity seam
// (`provideContributions`, `injectContributions`) as plain `@angular/core` runtime APIs, so it compiles with
// the same `tsc` build as the rest of the package — no ng-packagr, no second toolchain, one npm package.
// Rendering is the app's own `@for` block (idiomatic Angular 20+ control flow); a structural directive is a
// handful of lines the app writes on top of `injectContributions` (see the README) when it wants slot sugar.
import {
  computed,
  inject,
  InjectionToken,
  isSignal,
  makeEnvironmentProviders,
  signal,
  type EnvironmentProviders,
  type Signal,
} from "@angular/core"
import type { Contribution, ContributionRegistry, ExtensionPoint } from "./index.js"

// A Signal is held (never a bare registry) so a snapshot refresh — login, context change — re-resolves every
// slot reactively by setting one signal, the Angular mirror of React swapping the provider value.
const CONTRIBUTION_REGISTRY = new InjectionToken<Signal<ContributionRegistry>>(
  "elarion.contributions.registry"
)

/**
 * Provides the resolved registry to the injector tree — the idiomatic mirror of React's
 * `<ContributionProvider>`, shaped like `provideRouter`/`provideHttpClient`. Pass a live registry for a
 * static snapshot, or a `Signal<ContributionRegistry>` when the capability snapshot can change at runtime
 * (login, tenant switch): rebuilding the registry and setting the signal re-resolves every slot.
 *
 * @example
 * ```ts
 * bootstrapApplication(App, {
 *   providers: [provideContributions(createContributionRegistry(manifests, capabilities))],
 * })
 * ```
 */
export function provideContributions(
  source: ContributionRegistry | Signal<ContributionRegistry>
): EnvironmentProviders {
  const registry = isSignal(source) ? source : signal(source)
  return makeEnvironmentProviders([{ provide: CONTRIBUTION_REGISTRY, useValue: registry }])
}

/**
 * Reads a point's resolved contributions as a `Signal` — call from an injection context (a component field,
 * a `factory`, or inside `runInInjectionContext`). The signal tracks the provided registry, so a snapshot
 * refresh updates every template reading it. Already filtered by `when` and deterministically ordered.
 *
 * @example
 * ```ts
 * @Component({
 *   template: `@for (item of items(); track item.id) { <a [routerLink]="item.to">{{ item.label }}</a> }`,
 * })
 * export class Sidebar {
 *   readonly items = injectContributions(sidebarItems)
 * }
 * ```
 */
export function injectContributions<TItem, TContext>(
  point: ExtensionPoint<TItem, TContext>
): Signal<ReadonlyArray<Contribution<TItem>>> {
  const registry = inject(CONTRIBUTION_REGISTRY, { optional: true })
  if (registry === null) {
    throw new Error("injectContributions requires provideContributions() in the injector tree.")
  }
  return computed(() => registry().get(point))
}
