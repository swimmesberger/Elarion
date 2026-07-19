// The extension points the app shell itself owns. Elarion ships the point *mechanism*; the points — their
// payload shapes and the shell that renders them — are the application's (the no-UI-kit rule of
// ADR-0032): the shell is the frontend's platform module, and feature modules contribute into it exactly
// the way they contribute into each other.
import type {LucideIcon} from "lucide-react"
import {defineExtensionPoint} from "@/platform/contributions"

/**
 * A main-navigation entry — this app's payload for the sidebar slot. `to` stays a plain string so the
 * point remains router-agnostic data; the shell is the one place that hands it to the router's Link.
 */
export interface SidebarItem {
  readonly label: string
  readonly icon: LucideIcon
  readonly to: string
}

export const sidebarItems = defineExtensionPoint<SidebarItem>("platform.sidebar")
