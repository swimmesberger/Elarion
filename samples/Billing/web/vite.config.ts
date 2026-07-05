import path from "node:path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig } from "vite"

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { "@": path.resolve(__dirname, "./src") },
    // Hook-using libraries must resolve to this app's single React instance. This matters beyond the
    // symlinked file: install used here — Vite's dep optimizer has pre-bundled a second React for
    // published installs of @swimmesberger/elarion-contributions too, which surfaces as "Invalid hook
    // call" at the first useContributions. Keep the dedupe (and see the Vite note in the
    // frontend-modules concept doc).
    dedupe: ["react", "react-dom", "@tanstack/react-router"],
  },
  optimizeDeps: {
    // Pre-bundle the package and the app's React in the same optimization pass, so subpath exports
    // discovered late (deep in the module graph) never get their own React copy.
    include: [
      "@swimmesberger/elarion-contributions",
      "@swimmesberger/elarion-contributions/react",
      "@swimmesberger/elarion-contributions/tanstack-router",
    ],
  },
})
