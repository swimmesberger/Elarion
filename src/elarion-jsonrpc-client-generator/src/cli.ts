#!/usr/bin/env node

import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { basename, resolve } from 'node:path'
import { generateRpcClientFiles, type RpcSchema } from './generate.js'

interface CliOptions {
  schemaPath: string
  outDir: string
  typesFileName?: string
  schemasFileName?: string
  clientFileName?: string
  sessionClientFileName?: string
  sourceLabel?: string
}

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
  --source-label <text> Source label written into generated file headers
`)
}

function main() {
  const options = parseArgs(process.argv.slice(2))
  const schemaPath = resolve(process.cwd(), options.schemaPath)
  const outDir = resolve(process.cwd(), options.outDir)
  const raw = readFileSync(schemaPath, 'utf-8')
  const schema = JSON.parse(raw) as RpcSchema

  const generated = generateRpcClientFiles(schema, {
    sourceLabel: options.sourceLabel ?? basename(schemaPath),
    typesFileName: options.typesFileName,
    schemasFileName: options.schemasFileName,
    clientFileName: options.clientFileName,
    sessionClientFileName: options.sessionClientFileName,
  })

  mkdirSync(outDir, { recursive: true })
  writeFileSync(resolve(outDir, generated.typesFileName), generated.typesSource, 'utf-8')
  writeFileSync(resolve(outDir, generated.schemasFileName), generated.schemasSource, 'utf-8')
  writeFileSync(resolve(outDir, generated.clientFileName), generated.clientSource, 'utf-8')

  let sessionNote = ''
  if (generated.sessionClientFileName !== undefined && generated.sessionClientSource !== undefined) {
    writeFileSync(resolve(outDir, generated.sessionClientFileName), generated.sessionClientSource, 'utf-8')
    sessionNote = ` (+ ${generated.sessionClientFileName})`
  }

  console.log(
    `[jsonrpc-client-generator] Generated ${generated.methodCount} RPC method types and schemas${sessionNote} -> ${outDir}`
  )
}

main()
