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
//   commit <message>           Commit the staged changes onto the remote branch through the GitHub API
//                              (GITHUB_TOKEN, GITHUB_REPOSITORY; RELEASE_BRANCH defaults to main), then
//                              move the local checkout onto that commit.
//
// Paths resolve from the repository root (this file's parent directory).

import { execFileSync } from 'node:child_process';
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

// Rewrite the version of every Elarion package reference to the released version: the `Version="x.y.z"`
// literal of a <PackageReference> and the `--version x.y.z` of a `dotnet add package` command.
// Scoped to Elarion packages so third-party references in snippets keep their own versions.
function syncDocVersions(version) {
  const semver = String.raw`\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?`;
  const literals = [
    new RegExp(String.raw`(\bInclude="Elarion(?:\.[A-Za-z0-9.]+)?"\s+Version=")(?:${semver})(")`, 'g'),
    new RegExp(String.raw`(\bpackage Elarion(?:\.[A-Za-z0-9.]+)?\s+--version )(?:${semver})()`, 'g'),
  ];
  let changed = 0;
  for (const path of docFiles()) {
    const before = read(path);
    const after = literals.reduce((text, literal) => text.replace(literal, `$1${version}$2`), before);
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

const git = (...args) => execFileSync('git', args, { cwd: repoRoot, encoding: 'utf8' });

// A commit created through the createCommitOnBranch mutation with a GitHub App installation token is
// signed by GitHub, so the release commits show as Verified; a commit made by `git` on the runner and
// pushed is unsigned. expectedHeadOid also makes the mutation fail if the branch moved since checkout,
// so a release never lands on top of a commit it did not prepare.
async function commitViaApi(message) {
  const token = process.env.GITHUB_TOKEN;
  const repository = process.env.GITHUB_REPOSITORY;
  const branch = process.env.RELEASE_BRANCH || 'main';
  if (!token || !repository) fail('commit needs GITHUB_TOKEN and GITHUB_REPOSITORY');

  const additions = [];
  const deletions = [];
  const entries = git('diff', '--cached', '--name-status', '--no-renames', '-z').split('\0').filter(Boolean);
  for (let i = 0; i < entries.length; i += 2) {
    const [status, path] = [entries[i], entries[i + 1]];
    if (status === 'D') deletions.push({ path });
    else additions.push({ path, contents: readFileSync(join(repoRoot, path)).toString('base64') });
  }
  if (additions.length + deletions.length === 0) fail('commit: nothing staged');

  const [headline, ...body] = message.split('\n');
  const response = await fetch(process.env.GITHUB_GRAPHQL_URL || 'https://api.github.com/graphql', {
    method: 'POST',
    headers: { authorization: `bearer ${token}`, 'content-type': 'application/json' },
    body: JSON.stringify({
      query: `mutation ($input: CreateCommitOnBranchInput!) {
        createCommitOnBranch(input: $input) { commit { oid } }
      }`,
      variables: {
        input: {
          branch: { repositoryNameWithOwner: repository, branchName: branch },
          expectedHeadOid: git('rev-parse', 'HEAD').trim(),
          message: { headline, body: body.join('\n').trim() },
          fileChanges: { additions, deletions },
        },
      },
    }),
  });
  const result = await response.json();
  const oid = result.data?.createCommitOnBranch?.commit?.oid;
  if (!response.ok || !oid) fail(`createCommitOnBranch failed: ${JSON.stringify(result.errors ?? result)}`);

  git('fetch', '--quiet', 'origin', branch);
  if (git('rev-parse', `${oid}^{tree}`).trim() !== git('write-tree').trim()) {
    fail(`commit ${oid} does not match the staged tree`);
  }
  git('reset', '--quiet', '--hard', oid);
  console.log(`committed ${oid} to ${branch}`);
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
  case 'commit': {
    if (!version) fail('usage: release.mjs commit <message>');
    await commitViaApi(version);
    break;
  }
  default:
    fail(`unknown command '${command ?? ''}'. Use prepare | bump-prefix | notes | commit`);
}
