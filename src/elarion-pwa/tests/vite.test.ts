import { describe, expect, it } from 'vitest'
import { appVersionFile, type AppVersionEmittedAsset } from '../src/vite.js'

function build(plugin: ReturnType<typeof appVersionFile>, { environment, ssr = false }: { environment?: string; ssr?: boolean }) {
  const emitted: AppVersionEmittedAsset[] = []
  plugin.configResolved({ build: { ssr } })
  plugin.generateBundle.call({
    emitFile: (file) => {
      emitted.push(file)
      return file.fileName
    },
    environment: environment === undefined ? undefined : { name: environment },
  })
  return emitted
}

describe('appVersionFile', () => {
  it('emits the version file in the client build only', () => {
    const plugin = appVersionFile({ version: '2026-10-07.12 (abc1234)' })

    expect(build(plugin, { environment: 'client' })).toEqual([
      { type: 'asset', fileName: 'app-version.json', source: '{"version":"2026-10-07.12 (abc1234)"}' },
    ])
    expect(build(plugin, { environment: 'ssr' })).toEqual([])
  })

  it('tells an SSR build apart before the environment API', () => {
    const plugin = appVersionFile({ version: 'v1', fileName: 'version.json' })

    expect(build(plugin, { ssr: false }).map((file) => file.fileName)).toEqual(['version.json'])
    expect(build(plugin, { ssr: true })).toEqual([])
  })

  it('bakes the same version into the bundle when asked', () => {
    expect(appVersionFile({ version: 'v1', define: '__APP_VERSION__' }).config()).toEqual({
      define: { __APP_VERSION__: '"v1"' },
    })
    expect(appVersionFile({ version: 'v1' }).config()).toBeUndefined()
  })
})
