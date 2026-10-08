// A Vite plugin that publishes the build's version next to its assets (`/app-version.json`), which the page half's
// update watcher compares with the version baked into the running bundle. Optionally it bakes that same value in
// (`define`), so the two ends cannot drift apart. Typed structurally: this package does not import Vite.

/** Options for {@link appVersionFile}. */
export interface AppVersionFileOptions {
  /** The build's version — a CI run number, a commit hash, a date stamp. Normalize it once, here. */
  version: string
  /** The emitted file name, relative to the build output. Defaults to `app-version.json`. */
  fileName?: string
  /**
   * A global constant to define as the same version in the bundle, e.g. `'__APP_VERSION__'` (declare it in a
   * `.d.ts`: `declare const __APP_VERSION__: string`). Applies to the dev server as well.
   */
  define?: string
  /**
   * The Vite environment that emits the file (Vite 6+ environment API) — the one whose output the web server
   * serves. Defaults to `client`; an SSR environment never has a static directory to serve it from.
   */
  environment?: string
}

/** The emitted-file shape the plugin uses from Vite's plugin context. */
export interface AppVersionEmittedAsset {
  type: 'asset'
  fileName: string
  source: string
}

/** The plugin context the plugin uses. */
export interface AppVersionPluginContext {
  emitFile(file: AppVersionEmittedAsset): string
  readonly environment?: { readonly name: string }
}

/** The plugin {@link appVersionFile} returns — assignable to Vite's `Plugin`. */
export interface AppVersionPlugin {
  readonly name: string
  config(): { define: Record<string, string> } | undefined
  configResolved(config: { readonly build: { readonly ssr?: boolean | string } }): void
  generateBundle(this: AppVersionPluginContext): void
}

/**
 * Emits `{"version": "…"}` as `/app-version.json` in the client build, for `jsonVersionSource()` and
 * `createUpdateWatcher` from `@swimmesberger/elarion-pwa`.
 *
 * Serve the file with `Cache-Control: no-cache` (the watcher also fetches it with `cache: 'no-store'`), and keep it
 * on the web build's side of a reverse proxy — not routed to the API.
 *
 * @example
 * ```ts
 * // vite.config.ts
 * import { appVersionFile } from '@swimmesberger/elarion-pwa/vite'
 *
 * const version = process.env.APP_VERSION?.trim() || 'dev'
 * export default defineConfig({ plugins: [appVersionFile({ version, define: '__APP_VERSION__' }), react()] })
 * ```
 */
export function appVersionFile(options: AppVersionFileOptions): AppVersionPlugin {
  const fileName = options.fileName ?? 'app-version.json'
  const environment = options.environment ?? 'client'
  const source = JSON.stringify({ version: options.version })
  let ssrBuild = false

  return {
    name: 'elarion:app-version',
    config() {
      return options.define ? { define: { [options.define]: JSON.stringify(options.version) } } : undefined
    },
    configResolved(config) {
      ssrBuild = Boolean(config.build.ssr)
    },
    generateBundle() {
      // Vite 6+ runs one bundle per environment; before that, an SSR build is a separate `vite build --ssr`.
      const name = this.environment?.name
      if (name !== undefined ? name !== environment : ssrBuild) return
      this.emitFile({ type: 'asset', fileName, source })
    },
  }
}
