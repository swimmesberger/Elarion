import {
  createEnvironmentInjector,
  runInInjectionContext,
  signal,
  type EnvironmentInjector,
} from "@angular/core"
import {describe, expect, it} from "vitest"
import {
  contribute,
  createContributionRegistry,
  defineExtensionPoint,
  type CapabilityReader,
  type ContributionRegistry,
  type ModuleManifest,
} from "../src/index.js"
import {injectContributions, provideContributions} from "../src/angular.js"

function capabilities(granted: boolean): CapabilityReader {
  return {
    isModuleEnabled: () => granted,
    hasPermission: () => granted,
    hasRole: () => granted,
    isFlagEnabled: () => granted,
  }
}

interface Item {
  readonly label: string
}

const point = defineExtensionPoint<Item>("demo.point")

function registry(caps: CapabilityReader): ContributionRegistry {
  const manifest: ModuleManifest = {
    name: "demo",
    contributes: [
      contribute(point, [
        {id: "b", label: "B", order: 2},
        {id: "a", label: "A", order: 1},
      ]),
    ],
  }
  return createContributionRegistry([manifest], caps)
}

// A no-parent environment injector is enough to exercise the DI seam without a DOM, zone, or TestBed —
// the runtime tolerates the missing parent even though the signature demands one.
function read<T>(fn: () => T, ...providers: ReturnType<typeof provideContributions>[]): T {
  const injector = createEnvironmentInjector(providers, undefined as unknown as EnvironmentInjector)
  return runInInjectionContext(injector, fn)
}

describe("injectContributions", () => {
  it("returns the point's resolved contributions, deterministically ordered", () => {
    const items = read(() => injectContributions(point), provideContributions(registry(capabilities(true))))
    expect(items().map((i) => i.id)).toEqual(["a", "b"])
  })

  it("tracks a Signal registry so a snapshot refresh re-resolves the slot", () => {
    const source = signal(registry(capabilities(true)))
    const items = read(() => injectContributions(point), provideContributions(source))
    expect(items().map((i) => i.id)).toEqual(["a", "b"])

    source.set(createContributionRegistry([], capabilities(true)))
    expect(items()).toEqual([])
  })

  it("throws when no provideContributions() is in the injector tree", () => {
    expect(() => read(() => injectContributions(point))).toThrow(/requires provideContributions/)
  })
})
