import { describe, expect, it } from "vitest"
import {
  contribute,
  createContributionRegistry,
  defineExtensionPoint,
  defineModule,
  evaluateWhen,
  type CapabilityReader,
} from "../src/index.js"

function capabilities(granted: {
  modules?: string[]
  permissions?: string[]
  roles?: string[]
  flags?: string[]
}): CapabilityReader {
  return {
    isModuleEnabled: (name) => granted.modules?.includes(name) ?? false,
    hasPermission: (permission) => granted.permissions?.includes(permission) ?? false,
    hasRole: (role) => granted.roles?.includes(role) ?? false,
    isFlagEnabled: (name) => granted.flags?.includes(name) ?? false,
  }
}

interface Item {
  readonly label: string
}

const point = defineExtensionPoint<Item>("test.point")

describe("evaluateWhen", () => {
  it("passes with no clause", () => {
    expect(evaluateWhen(undefined, capabilities({}))).toBe(true)
  })

  it("ANDs every present field", () => {
    const caps = capabilities({ modules: ["A"], permissions: ["a.read"] })
    expect(evaluateWhen({ module: "A", permission: "a.read" }, caps)).toBe(true)
    expect(evaluateWhen({ module: "A", permission: "a.write" }, caps)).toBe(false)
    expect(evaluateWhen({ module: "B", permission: "a.read" }, caps)).toBe(false)
    expect(evaluateWhen({ role: "admin" }, caps)).toBe(false)
    expect(evaluateWhen({ flag: "beta" }, caps)).toBe(false)
  })
})

describe("createContributionRegistry", () => {
  it("returns an empty list for a point nothing contributes to", () => {
    const registry = createContributionRegistry([], capabilities({}))
    expect(registry.get(point)).toEqual([])
  })

  it("filters by the contribution's when clause", () => {
    const manifest = defineModule({
      name: "A",
      contributes: [
        contribute(point, [
          { id: "visible", label: "Visible", when: { permission: "a.read" } },
          { id: "hidden", label: "Hidden", when: { permission: "a.write" } },
        ]),
      ],
    })
    const registry = createContributionRegistry([manifest], capabilities({ permissions: ["a.read"] }))
    expect(registry.get(point).map((item) => item.id)).toEqual(["visible"])
  })

  it("ANDs the manifest-level when into every contribution", () => {
    const manifest = defineModule({
      name: "A",
      when: { module: "A" },
      contributes: [contribute(point, [{ id: "item", label: "Item" }])],
    })
    expect(
      createContributionRegistry([manifest], capabilities({ modules: ["A"] })).get(point)
    ).toHaveLength(1)
    expect(createContributionRegistry([manifest], capabilities({})).get(point)).toHaveLength(0)
  })

  it("orders by order, then id, then contributing module — independent of manifest order", () => {
    const first = defineModule({
      name: "B",
      contributes: [
        contribute(point, [
          { id: "z", label: "Z", order: 10 },
          { id: "same", label: "From B", order: 20 },
        ]),
      ],
    })
    const second = defineModule({
      name: "A",
      contributes: [
        contribute(point, [
          { id: "a", label: "A", order: 10 },
          { id: "same", label: "From A", order: 20 },
        ]),
      ],
    })
    const resolve = (manifests: Parameters<typeof createContributionRegistry>[0]) =>
      createContributionRegistry(manifests, capabilities({})).get(point).map((item) => item.label)
    expect(resolve([first, second])).toEqual(["A", "Z", "From A", "From B"])
    expect(resolve([second, first])).toEqual(["A", "Z", "From A", "From B"])
  })

  it("merges contributions to the same point across modules", () => {
    const owner = defineModule({
      name: "Owner",
      contributes: [contribute(point, [{ id: "own", label: "Own" }])],
    })
    const contributor = defineModule({
      name: "Contributor",
      contributes: [contribute(point, [{ id: "extra", label: "Extra" }])],
    })
    const registry = createContributionRegistry([owner, contributor], capabilities({}))
    expect(registry.get(point).map((item) => item.id)).toEqual(["extra", "own"])
  })
})
