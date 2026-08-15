import {describe, expect, it, vi} from "vitest"
import {
  contribute,
  createCapabilityStore,
  createContributionRegistry,
  createContributionRegistryStore,
  createStaticCapabilities,
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
    const caps = capabilities({modules: ["A"], permissions: ["a.read"]})
    expect(evaluateWhen({module: "A", permission: "a.read"}, caps)).toBe(true)
    expect(evaluateWhen({module: "A", permission: "a.write"}, caps)).toBe(false)
    expect(evaluateWhen({module: "B", permission: "a.read"}, caps)).toBe(false)
    expect(evaluateWhen({role: "admin"}, caps)).toBe(false)
    expect(evaluateWhen({flag: "beta"}, caps)).toBe(false)
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
          {id: "visible", label: "Visible", when: {permission: "a.read"}},
          {id: "hidden", label: "Hidden", when: {permission: "a.write"}},
        ]),
      ],
    })
    const registry = createContributionRegistry([manifest], capabilities({permissions: ["a.read"]}))
    expect(registry.get(point).map((item) => item.id)).toEqual(["visible"])
  })

  it("ANDs the manifest-level when into every contribution", () => {
    const manifest = defineModule({
      name: "A",
      when: {module: "A"},
      contributes: [contribute(point, [{id: "item", label: "Item"}])],
    })
    expect(
      createContributionRegistry([manifest], capabilities({modules: ["A"]})).get(point)
    ).toHaveLength(1)
    expect(createContributionRegistry([manifest], capabilities({})).get(point)).toHaveLength(0)
  })

  it("orders by order, then id — independent of manifest order", () => {
    const first = defineModule({
      name: "B",
      contributes: [
        contribute(point, [
          {id: "z", label: "Z", order: 10},
          {id: "b.tail", label: "From B", order: 20},
        ]),
      ],
    })
    const second = defineModule({
      name: "A",
      contributes: [
        contribute(point, [
          {id: "a", label: "A", order: 10},
          {id: "a.tail", label: "From A", order: 20},
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
      contributes: [contribute(point, [{id: "own", label: "Own"}])],
    })
    const contributor = defineModule({
      name: "Contributor",
      contributes: [contribute(point, [{id: "extra", label: "Extra"}])],
    })
    const registry = createContributionRegistry([owner, contributor], capabilities({}))
    expect(registry.get(point).map((item) => item.id)).toEqual(["extra", "own"])
  })

  it("throws when two co-visible contributions to one point share an id", () => {
    const a = defineModule({
      name: "A",
      contributes: [contribute(point, [{id: "same", label: "From A"}])],
    })
    const b = defineModule({
      name: "B",
      contributes: [contribute(point, [{id: "same", label: "From B"}])],
    })
    expect(() => createContributionRegistry([a, b], capabilities({}))).toThrowError(
      /Duplicate contribution id "same" on extension point "test\.point".*module "A".*module "B"/
    )
  })

  it("allows a shared id when a when clause hides one of the pair", () => {
    const a = defineModule({
      name: "A",
      contributes: [contribute(point, [{id: "same", label: "From A"}])],
    })
    const b = defineModule({
      name: "B",
      when: {module: "B"},
      contributes: [contribute(point, [{id: "same", label: "From B"}])],
    })
    const registry = createContributionRegistry([a, b], capabilities({}))
    expect(registry.get(point).map((item) => item.label)).toEqual(["From A"])
  })
})

describe("createCapabilityStore", () => {
  it("delegates every read to the current reader", () => {
    const store = createCapabilityStore(capabilities({modules: ["A"]}))
    expect(store.isModuleEnabled("A")).toBe(true)
    expect(store.isModuleEnabled("B")).toBe(false)

    store.set(capabilities({modules: ["B"], permissions: ["a.read"], roles: ["admin"], flags: ["beta"]}))
    expect(store.isModuleEnabled("A")).toBe(false)
    expect(store.isModuleEnabled("B")).toBe(true)
    expect(store.hasPermission("a.read")).toBe(true)
    expect(store.hasRole("admin")).toBe(true)
    expect(store.isFlagEnabled("beta")).toBe(true)
  })

  it("notifies subscribers on set, and is a no-op for the same reference", () => {
    const reader = capabilities({})
    const store = createCapabilityStore(reader)
    const listener = vi.fn()
    store.subscribe(listener)

    store.set(reader)
    expect(listener).not.toHaveBeenCalled()

    store.set(capabilities({modules: ["A"]}))
    expect(listener).toHaveBeenCalledTimes(1)
  })

  it("stops notifying after unsubscribe", () => {
    const store = createCapabilityStore(capabilities({}))
    const listener = vi.fn()
    const unsubscribe = store.subscribe(listener)

    store.set(capabilities({modules: ["A"]}))
    unsubscribe()
    store.set(capabilities({modules: ["B"]}))

    expect(listener).toHaveBeenCalledTimes(1)
  })

  it("drives a route guard's context.caps without the context being rebuilt", () => {
    // The store's identity is the point: a router context holds it once, and later reads see new answers.
    const store = createCapabilityStore(capabilities({}))
    const context = {caps: store}
    expect(evaluateWhen({module: "A"}, context.caps)).toBe(false)
    store.set(capabilities({modules: ["A"]}))
    expect(evaluateWhen({module: "A"}, context.caps)).toBe(true)
  })
})

describe("createContributionRegistryStore", () => {
  const manifest = defineModule({
    name: "A",
    contributes: [contribute(point, [{id: "beta", label: "Beta", when: {flag: "beta"}}])],
  })

  it("rebuilds when the capability source changes — a contribution appears and disappears", () => {
    const caps = createCapabilityStore(capabilities({}))
    const store = createContributionRegistryStore([manifest], caps)

    expect(store.current.get(point)).toHaveLength(0)

    caps.set(capabilities({flags: ["beta"]}))
    expect(store.current.get(point).map((item) => item.id)).toEqual(["beta"])

    caps.set(capabilities({}))
    expect(store.current.get(point)).toHaveLength(0)
  })

  it("keeps current referentially stable between changes", () => {
    const caps = createCapabilityStore(capabilities({flags: ["beta"]}))
    const store = createContributionRegistryStore([manifest], caps)

    // useSyncExternalStore calls getSnapshot repeatedly per render; an unstable identity would loop forever.
    expect(store.current).toBe(store.current)

    const before = store.current
    caps.set(capabilities({}))
    expect(store.current).not.toBe(before)
  })

  it("notifies subscribers when the source changes, and stops after unsubscribe", () => {
    const caps = createCapabilityStore(capabilities({}))
    const store = createContributionRegistryStore([manifest], caps)
    const listener = vi.fn()
    const unsubscribe = store.subscribe(listener)

    caps.set(capabilities({flags: ["beta"]}))
    expect(listener).toHaveBeenCalledTimes(1)

    unsubscribe()
    caps.set(capabilities({}))
    expect(listener).toHaveBeenCalledTimes(1)
  })

  it("resolves through the pure registry, so a duplicate id still throws — at the read", () => {
    const a = defineModule({name: "A", contributes: [contribute(point, [{id: "same", label: "A"}])]})
    const b = defineModule({name: "B", contributes: [contribute(point, [{id: "same", label: "B"}])]})
    const store = createContributionRegistryStore([a, b], createCapabilityStore(capabilities({})))

    expect(() => store.current).toThrowError(/Duplicate contribution id "same"/)
  })
})

describe("createStaticCapabilities", () => {
  it("defaults modules, permissions, and roles open and flags closed", () => {
    const caps = createStaticCapabilities()
    expect(caps.isModuleEnabled("anything")).toBe(true)
    expect(caps.hasPermission("anything")).toBe(true)
    expect(caps.hasRole("anything")).toBe(true)
    expect(caps.isFlagEnabled("anything")).toBe(false)
  })

  it("accepts an allow-list per axis", () => {
    const caps = createStaticCapabilities({modules: ["core"], flags: ["beta"]})
    expect(caps.isModuleEnabled("core")).toBe(true)
    expect(caps.isModuleEnabled("other")).toBe(false)
    expect(caps.isFlagEnabled("beta")).toBe(true)
    expect(caps.isFlagEnabled("other")).toBe(false)
  })

  it("accepts an explicit on/off map — unlisted names are off", () => {
    const caps = createStaticCapabilities({modules: {core: true, "ai-agent": false}})
    expect(caps.isModuleEnabled("core")).toBe(true)
    expect(caps.isModuleEnabled("ai-agent")).toBe(false)
    expect(caps.isModuleEnabled("unlisted")).toBe(false)
  })

  it('accepts "all"', () => {
    const caps = createStaticCapabilities({flags: "all"})
    expect(caps.isFlagEnabled("anything")).toBe(true)
  })

  it("drives registry resolution like any other reader", () => {
    const manifest = defineModule({
      name: "A",
      when: {module: "A"},
      contributes: [contribute(point, [{id: "item", label: "Item", when: {flag: "beta"}}])],
    })
    const off = createContributionRegistry([manifest], createStaticCapabilities())
    expect(off.get(point)).toHaveLength(0)
    const on = createContributionRegistry([manifest], createStaticCapabilities({flags: ["beta"]}))
    expect(on.get(point)).toHaveLength(1)
  })
})
