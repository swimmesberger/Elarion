// The root of the route tree. Modules attach their own subtrees here (getParentRoute), and the composition
// root in app.tsx assembles them. The router context carries the capability snapshot so a route's
// beforeLoad guard can mirror its sidebar item's `when` clause.
import {createRootRouteWithContext} from "@tanstack/react-router"
import {createRouteGuards} from "@swimmesberger/elarion-contributions/tanstack-router"
import type {SessionCapabilities} from "@/generated/session-client"
import {AppShell} from "@/platform/AppShell"
import type {AppVocabulary} from "@/platform/contributions"

export interface RouterContext {
  readonly caps: SessionCapabilities
}

export const rootRoute = createRootRouteWithContext<RouterContext>()({
  component: AppShell,
})

// Route guards bound to the generated vocabulary — a route declares the same typed `when` clause as its
// sidebar item, so nav visibility and deep-link gating can never disagree.
export const {redirectUnless} = createRouteGuards<AppVocabulary>()
