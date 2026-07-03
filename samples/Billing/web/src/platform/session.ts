// Fetches the client-capability snapshot (ADR-0030) once at boot and wraps it in the generated typed
// accessors. The snapshot is a read-only UX projection — every handler still enforces its own
// [RequirePermission]/[FeatureGate] server-side; hiding a button here secures nothing.
import {
  createSessionCapabilities,
  type ClientSnapshot,
  type SessionCapabilities,
} from "@/generated/session-client"
import { rpc } from "@/lib/rpc"

export type { SessionCapabilities }

// Fail closed: when the API is unreachable the shell still renders, with every gated contribution hidden.
const OFFLINE: ClientSnapshot = {
  user: { id: "", isAuthenticated: false, roles: [], permissions: [] },
  modules: {},
  flags: {},
  variants: {},
}

export async function loadCapabilities(): Promise<SessionCapabilities> {
  try {
    // The generated method map types dictionary-shaped fields as `unknown`; session-client.ts is the typed
    // view of the same payload.
    const snapshot = (await rpc.elarion.session({})) as ClientSnapshot
    return createSessionCapabilities(snapshot)
  } catch (error) {
    console.error("Failed to load the capability snapshot — rendering with everything off.", error)
    return createSessionCapabilities(OFFLINE)
  }
}
