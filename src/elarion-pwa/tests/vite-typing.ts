// Type-checked only (tsconfig.test.json): the structurally typed plugin must be accepted by Vite's own config type,
// without this package importing Vite at runtime.
import { defineConfig, type Plugin } from 'vite'
import { appVersionFile } from '../src/vite.js'

const plugin: Plugin = appVersionFile({ version: 'v1', define: '__APP_VERSION__' })

export default defineConfig({ plugins: [plugin, appVersionFile({ version: 'v1' })] })
