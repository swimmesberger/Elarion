#!/usr/bin/env node
// Release helper for Elarion. Plain Node, no dependencies.
//
// `<VersionPrefix>` in Directory.Build.props is the single source of truth for the
// *next* version: pushes to main publish it as `{VersionPrefix}-preview.*`, and a
// release promotes it to a stable, tagged, doc-referenced version.
//
// Subcommands:
//   prepare <version> [date]   Set VersionPrefix, sync the doc package-version literals
//                              to <version>, and roll the changelog "[Unreleased]" section
//                              into a dated "[<version>]" release. [date] defaults to today.
//   bump-prefix <version>      Set VersionPrefix only (used to open the next dev cycle).
//   notes <version>            Print the changelog body for <version> to stdout (release notes).
//
// Paths resolve from the repository root (this file's parent directory).

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const STABLE_SEMVER = /^\d+\.\d+\.\d+$/;

function fail(message) {
  console.error(`error: ${message}`);
  process.exit(1);
}

function assertStable(version) {
  if (!STABLE_SEMVER.test(version)) {
    fail(`version '${version}' must be a stable MAJOR.MINOR.PATCH (no prerelease suffix)`);
  }
}

const read = (path) => readFileSync(join(repoRoot, path), 'utf8');
const write = (path, content) => writeFileSync(join(repoRoot, path), content);

function setVersionPrefix(version) {
  const tag = /<VersionPrefix>[^<]*<\/VersionPrefix>/;
  const before = read('Directory.Build.props');
  // Test for presence, not a diff: in the normal flow VersionPrefix already equals
  // the release version, so the replacement is a deliberate no-op.
  if (!tag.test(before)) fail('could not find <VersionPrefix> in Directory.Build.props');
  write('Directory.Build.props', before.replace(tag, `<VersionPrefix>${version}</VersionPrefix>`));
}

// README plus every Markdown/MDX file under docs/ — the surfaces that show install snippets.
function docFiles() {
  const files = ['README.md'];
  const walk = (dir) => {
    for (const entry of readdirSync(join(repoRoot, dir))) {
      const rel = join(dir, entry);
      if (statSync(join(repoRoot, rel)).isDirectory()) walk(rel);
      else if (/\.mdx?$/.test(entry)) files.push(rel);
    }
  };
  walk('docs');
  return files;
}

// Rewrite every `Version="x.y.z"` package-reference literal to the released version.
function syncDocVersions(version) {
  const literal = /(\bVersion=")(?:\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)(")/g;
  let changed = 0;
  for (const path of docFiles()) {
    const before = read(path);
    const after = before.replace(literal, `$1${version}$2`);
    if (after !== before) {
      write(path, after);
      changed += 1;
    }
  }
  return changed;
}

// Move the "[Unreleased]" entries under a dated "[version]" heading and refresh the
// link-reference definitions (Keep a Changelog style), leaving a fresh empty Unreleased.
function rollChangelog(version, date) {
  let text = read('CHANGELOG.md');
  if (!/^## \[Unreleased\]/m.test(text)) {
    fail('CHANGELOG.md is missing a "## [Unreleased]" heading');
  }
  if (new RegExp(`^## \\[${version.replace(/\./g, '\\.')}\\]`, 'm').test(text)) {
    console.log(`CHANGELOG.md already has a ${version} section — leaving it untouched`);
    return;
  }

  text = text.replace(/^## \[Unreleased\][^\n]*$/m, `## [Unreleased]\n\n## [${version}] - ${date}`);

  const unreleasedLink = text.match(/^\[Unreleased\]:\s*(\S+)\s*$/m);
  if (unreleasedLink) {
    const base = unreleasedLink[1].split('/compare/')[0];
    text = text.replace(
      /^\[Unreleased\]:\s*\S+\s*$/m,
      `[Unreleased]: ${base}/compare/v${version}...HEAD\n[${version}]: ${base}/releases/tag/v${version}`,
    );
  }

  write('CHANGELOG.md', text);
}

function printNotes(version) {
  const lines = read('CHANGELOG.md').split('\n');
  const escaped = version.replace(/\./g, '\\.');
  const start = lines.findIndex((line) => new RegExp(`^## \\[${escaped}\\]`).test(line));
  if (start === -1) fail(`CHANGELOG.md has no section for ${version}`);
  let end = lines.length;
  for (let i = start + 1; i < lines.length; i += 1) {
    if (/^## /.test(lines[i])) {
      end = i;
      break;
    }
  }
  process.stdout.write(`${lines.slice(start + 1, end).join('\n').trim()}\n`);
}

const [command, version, dateArg] = process.argv.slice(2);

switch (command) {
  case 'prepare': {
    if (!version) fail('usage: release.mjs prepare <version> [date]');
    assertStable(version);
    const date = dateArg || new Date().toISOString().slice(0, 10);
    setVersionPrefix(version);
    const changed = syncDocVersions(version);
    rollChangelog(version, date);
    console.log(`prepared release ${version} (synced ${changed} doc file(s), dated ${date})`);
    break;
  }
  case 'bump-prefix': {
    if (!version) fail('usage: release.mjs bump-prefix <version>');
    assertStable(version);
    setVersionPrefix(version);
    console.log(`set VersionPrefix to ${version}`);
    break;
  }
  case 'notes': {
    if (!version) fail('usage: release.mjs notes <version>');
    printNotes(version);
    break;
  }
  default:
    fail(`unknown command '${command ?? ''}'. Use prepare | bump-prefix | notes`);
}
