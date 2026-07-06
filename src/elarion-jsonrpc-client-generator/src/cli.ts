#!/usr/bin/env node

import { mkdirSync, readFileSync, watchFile, writeFileSync } from 'node:fs'
import { basename, resolve } from 'node:path'
import { generateRpcClientFiles, type RpcSchema } from './generate.js'

interface CliOptions {
  schemaPath: string
  outDir: string
  typesFileName?: string
  schemasFileName?: string
  clientFileName?: string
  sessionClientFileName?: string
  eventsClientFileName?: string
  frameworkAdapterFileName?: string
  framework?: 'tanstack-start'
  sourceLabel?: string
  watch?: boolean
}

// Poll interval for --watch. Polling (rather than fs.watch) is deliberate: the schema is (re)written by a
// build tool, often via a temp-file + rename, which event-based watchers miss or double-fire; polling the
// exact path is robust to that and to delete/recreate, at the cost of up-to-one-interval latency.
const WATCH_INTERVAL_MS = 300

const SUPPORTED_FRAMEWORKS = ['tanstack-start'] as const

function parseArgs(argv: string[]): CliOptions {
  const options: CliOptions = {
    schemaPath: 'rpc-schema.json',
    outDir: 'src/generated',
  }

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index]
    const next = argv[index + 1]

    if (arg === '--help' || arg === '-h') {
      printHelp()
      process.exit(0)
    }
    if (arg === '--watch') {
      options.watch = true
      continue
    }

    if (!next) {
      throw new Error(`Missing value for ${arg}`)
    }

    if (arg === '--schema') {
      options.schemaPath = next
      index += 1
      continue
    }
    if (arg === '--out') {
      options.outDir = next
      index += 1
      continue
    }
    if (arg === '--types') {
      options.typesFileName = next
      index += 1
      continue
    }
    if (arg === '--schemas') {
      options.schemasFileName = next
      index += 1
      continue
    }
    if (arg === '--client') {
      options.clientFileName = next
      index += 1
      continue
    }
    if (arg === '--session-client') {
      options.sessionClientFileName = next
      index += 1
      continue
    }
    if (arg === '--events-client') {
      options.eventsClientFileName = next
      index += 1
      continue
    }
    if (arg === '--framework') {
      if (!(SUPPORTED_FRAMEWORKS as readonly string[]).includes(next)) {
        throw new Error(`Unsupported framework ${next}. Supported: ${SUPPORTED_FRAMEWORKS.join(', ')}`)
      }
      options.framework = next as CliOptions['framework']
      index += 1
      continue
    }
    if (arg === '--framework-adapter') {
      options.frameworkAdapterFileName = next
      index += 1
      continue
    }
    if (arg === '--source-label') {
      options.sourceLabel = next
      index += 1
      continue
    }

    throw new Error(`Unknown argument ${arg}`)
  }

  return options
}

function printHelp() {
  console.log(`Usage: elarion-jsonrpc-client-generator [options]

Options:
  --schema <path>       Path to rpc-schema.json (default: rpc-schema.json)
  --out <dir>           Output directory (default: src/generated)
  --types <file>        TypeScript types filename (default: rpc-types.ts)
  --schemas <file>      Zod schemas filename (default: rpc-schemas.ts)
  --client <file>       Fetch client filename (default: rpc-client.ts)
  --session-client <file> Client-capability snapshot client + OpenFeature provider (default: session-client.ts;
                          emitted only when the schema exposes the elarion.session operation)
  --events-client <file>  Typed client-event subscription client (default: events-client.ts;
                          emitted only when the schema declares an events block)
  --framework <name>    Emit an opt-in framework adapter alongside the neutral core client.
                          Supported: tanstack-start (needs the @tanstack/react-start peer dependency)
  --framework-adapter <file> Framework adapter filename (default: start-adapter.ts)
  --source-label <text> Source label written into generated file headers
  --watch               Regenerate whenever the schema file changes (Ctrl+C to stop)
`)
}

// Reads the schema and writes the generated files once. Returns false (without throwing) on a missing or
// malformed schema so --watch can keep running across the transient states a build tool leaves the file in
// mid-write, and so a one-shot run exits non-zero with a clean message instead of a stack trace.
function generateOnce(options: CliOptions): boolean {
  const schemaPath = resolve(process.cwd(), options.schemaPath)
  const outDir = resolve(process.cwd(), options.outDir)

  let raw: string
  try {
    raw = readFileSync(schemaPath, 'utf-8')
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code
    console.error(
      code === 'ENOENT'
        ? `[jsonrpc-client-generator] Schema not found at ${schemaPath}`
        : `[jsonrpc-client-generator] Cannot read ${schemaPath}: ${errorMessage(error)}`
    )
    return false
  }

  let schema: RpcSchema
  try {
    schema = JSON.parse(raw) as RpcSchema
  } catch (error) {
    console.error(`[jsonrpc-client-generator] Invalid JSON in ${schemaPath}: ${errorMessage(error)}`)
    return false
  }

  const generated = generateRpcClientFiles(schema, {
    sourceLabel: options.sourceLabel ?? basename(schemaPath),
    typesFileName: options.typesFileName,
    schemasFileName: options.schemasFileName,
    clientFileName: options.clientFileName,
    sessionClientFileName: options.sessionClientFileName,
    eventsClientFileName: options.eventsClientFileName,
    framework: options.framework,
    frameworkAdapterFileName: options.frameworkAdapterFileName,
  })

  mkdirSync(outDir, { recursive: true })
  writeFileSync(resolve(outDir, generated.typesFileName), generated.typesSource, 'utf-8')
  writeFileSync(resolve(outDir, generated.schemasFileName), generated.schemasSource, 'utf-8')
  writeFileSync(resolve(outDir, generated.clientFileName), generated.clientSource, 'utf-8')

  const extraNotes: string[] = []
  if (generated.sessionClientFileName !== undefined && generated.sessionClientSource !== undefined) {
    writeFileSync(resolve(outDir, generated.sessionClientFileName), generated.sessionClientSource, 'utf-8')
    extraNotes.push(generated.sessionClientFileName)
  }
  if (generated.eventsClientFileName !== undefined && generated.eventsClientSource !== undefined) {
    writeFileSync(resolve(outDir, generated.eventsClientFileName), generated.eventsClientSource, 'utf-8')
    extraNotes.push(generated.eventsClientFileName)
  }
  if (generated.frameworkAdapterFileName !== undefined && generated.frameworkAdapterSource !== undefined) {
    writeFileSync(resolve(outDir, generated.frameworkAdapterFileName), generated.frameworkAdapterSource, 'utf-8')
    extraNotes.push(generated.frameworkAdapterFileName)
  }

  const extraNote = extraNotes.length > 0 ? ` (+ ${extraNotes.join(', ')})` : ''
  console.log(
    `[jsonrpc-client-generator] Generated ${generated.methodCount} RPC method types and schemas${extraNote} -> ${outDir}`
  )
  return true
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

function main() {
  const options = parseArgs(process.argv.slice(2))
  const ok = generateOnce(options)

  if (!options.watch) {
    if (!ok) {
      process.exitCode = 1
    }
    return
  }

  const schemaPath = resolve(process.cwd(), options.schemaPath)
  console.log(`[jsonrpc-client-generator] Watching ${schemaPath} for changes (Ctrl+C to stop)…`)
  watchFile(schemaPath, { interval: WATCH_INTERVAL_MS }, (curr, prev) => {
    // mtimeMs === 0 means the file does not currently exist (not yet written, or just deleted); wait for the
    // next write rather than regenerating from a stale/absent file.
    if (curr.mtimeMs === 0) {
      return
    }
    if (curr.mtimeMs === prev.mtimeMs && curr.size === prev.size) {
      return
    }
    generateOnce(options)
  })
}

main()
