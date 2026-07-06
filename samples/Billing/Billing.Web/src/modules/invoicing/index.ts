// The Invoicing module's public entry. It publishes only the module itself — Invoicing consumes other
// modules' contracts (the Clients row-action point) but currently offers none of its own.
import type { AppModule } from "@/platform/modules"
import { invoicingManifest, invoicingRoute } from "./module"

const invoicingModule = { manifest: invoicingManifest, routes: [invoicingRoute] } satisfies AppModule
export default invoicingModule
