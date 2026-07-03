import { describe, expect, it } from "vitest"
import type { CapabilityReader } from "../src/index.js"
import { createRouteGuards, redirectUnless } from "../src/tanstack-router.js"

function capabilities(granted: boolean): CapabilityReader {
  return {
    isModuleEnabled: () => granted,
    hasPermission: () => granted,
    hasRole: () => granted,
    isFlagEnabled: () => granted,
  }
}

describe("redirectUnless", () => {
  it("passes when the clause holds", () => {
    const guard = redirectUnless({ module: "A", permission: "a.read" }, "/")
    expect(() => guard({ context: { caps: capabilities(true) } })).not.toThrow()
  })

  it("throws a TanStack redirect to the given target when the clause fails", () => {
    const guard = redirectUnless({ module: "A" }, "/home")
    let thrown: unknown
    try {
      guard({ context: { caps: capabilities(false) } })
    } catch (error) {
      thrown = error
    }
    // TanStack redirects are Response objects carrying the navigation options.
    expect(thrown).toBeInstanceOf(Response)
    expect((thrown as { options: { to: string } }).options.to).toBe("/home")
  })

  it("is exposed identically through the vocabulary-bound factory", () => {
    const { redirectUnless: bound } = createRouteGuards<{
      module: "A" | "B"
      permission: string
      flag: string
      role: string
    }>()
    const guard = bound({ module: "A" }, "/")
    expect(() => guard({ context: { caps: capabilities(true) } })).not.toThrow()
  })
})
