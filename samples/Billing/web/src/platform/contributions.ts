// The app's one contribution kit — the @swimmesberger/elarion-contributions kernel bound to the generated
// capability vocabulary, so every `when` clause in a manifest is compile-checked against the catalog the
// backend actually enforces (ADR-0032): `when: { permission: "invocies.read" }` is a type error here, not
// a silently hidden item. Modules import defineModule/defineExtensionPoint/contribute from this file,
// never from the package directly.
import type { FlagName, ModuleName, PermissionName, RoleName } from "@/generated/session-client"
import { createContributionKit, type ModuleManifest } from "@swimmesberger/elarion-contributions"

export interface AppVocabulary {
  module: ModuleName
  permission: PermissionName
  flag: FlagName
  role: RoleName
}

export type AppManifest = ModuleManifest<AppVocabulary>

export const { defineModule, defineExtensionPoint, contribute } = createContributionKit<AppVocabulary>()
