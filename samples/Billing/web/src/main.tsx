import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { RouterProvider } from "@tanstack/react-router"
import { Toaster } from "@/components/ui/sonner"
import { createContributionRegistry } from "@swimmesberger/elarion-contributions"
import { ContributionProvider } from "@swimmesberger/elarion-contributions/react"
import { loadCapabilities } from "@/platform/session"
import { appModules, router } from "./app"
import "./index.css"

const queryClient = new QueryClient()

// One snapshot per boot gates contributions (the registry) and routes (the router context) alike.
// Refreshing after login or a context change means fetching again and rebuilding both.
const caps = await loadCapabilities()
const registry = createContributionRegistry(
  appModules.map((module) => module.manifest),
  caps
)

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <ContributionProvider registry={registry}>
        <RouterProvider router={router} context={{ caps }} />
        <Toaster />
      </ContributionProvider>
    </QueryClientProvider>
  </StrictMode>
)
