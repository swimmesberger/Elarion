import path from "node:path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig } from "vite"

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { "@": path.resolve(__dirname, "./src") },
    // @swimmesberger/elarion-contributions is consumed as a symlinked file: dependency here, so its
    // React and router peer imports must dedupe onto this app's copies (a published install needs
    // none of this).
    dedupe: ["react", "react-dom", "@tanstack/react-router"],
  },
})
