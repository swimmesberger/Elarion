# AGENTS.md — Elarion

Elarion is a reusable .NET application framework, delivered as NuGet and npm packages: an application writes
handlers inside modules, source generators emit the wiring, and opt-in packages add transports, persistence,
events and actors. People start at `README.md` and `CONTRIBUTING.md`. Agents: this file and the files it
points to are your instructions.

This file holds the rules that apply to every task and pointers, phrased by task, to everything else. Rules
for one kind of file live in a pattern rule under `.github/instructions/`. Add or change mandatory guidance
here or in a pattern rule, never in a router, a mirror or a pointer file.

## Layout

- `src/`: the packages. `Elarion.Abstractions` (contracts), `Elarion` (core), provider, protocol and host
  packages, the `*.Generators` projects bundled by them, and the npm packages (`src/elarion-*`).
- `tests/`: `Elarion.Tests` (the one test project) and `Elarion.Benchmarks`.
- `samples/`: applications that show the intended use of the framework.
- `docs/`: project knowledge beyond the code, and the content source of the documentation website. Two kinds
  of pages, told apart by extension: `.mdx` pages are published and describe shipped behavior; `.md` pages
  (decisions, research, architecture, the workflow) are repository knowledge, kept as an Open Knowledge Format
  bundle. `docs/index.md` is the map, `docs/guide.md` says what lives where and how the folder is kept.
- `docs/decisions/`: the ADRs. Not `docs/adr/`.
- `website/`: the documentation website, which renders `docs/`.
- `specs/`: what a new feature must do, and how it is built; format in `specs/README.md`.
- `GLOSSARY.md`: the glossary.
- `.github/instructions/*.instructions.md`: the pattern rules, each scoped to the files its `applyTo` matches.
  `.claude/rules/*.md` mirrors them for Claude Code and only points at them.
- `.agents/skills/`: procedures in the Agent Skills format. `.claude/skills` is a symlink to it.
- `tools/`: `check_repo.py` keeps this layout consistent, `check_commits.py` checks commit messages,
  `log_add.py` adds log entries, `hooks/` holds the commit-msg hook, `tests/` the tests of these tools.
  `scripts/` holds the release script.
- `CLAUDE.md` files: one-line routers (`@AGENTS.md`). Never put content in them; a `CLAUDE.md` with content of
  its own stops Claude Code from reading the `AGENTS.md` next to it. A folder that gets rules of its own gets
  an `AGENTS.md` and a router next to it.

## Commands

The repository's own checks, all run by `make check` (they need `python3` and nothing else):

- Tests of the tools: `python3 -m unittest discover -s tools/tests`
- Repository consistency: `python3 tools/check_repo.py`
- Commit messages of this branch, against the remote's default branch: `make check-commits` (needs the remote,
  so not part of `make check`)

The framework's validation. The complete contributor workflow, and what to do when the machine picks the wrong
SDK, is in [`CONTRIBUTING.md`](CONTRIBUTING.md). Run the relevant checks before requesting review; the normal
baseline is:

```bash
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release
dotnet pack Elarion.slnx --configuration Release --no-build

cd src/elarion-jsonrpc-client-generator
npm ci && npm run build && npm test && npm pack --dry-run
```

Run `dotnet run --project tests/Elarion.Benchmarks -c Release` for relevant performance-sensitive changes. The `net11.0`
build compiles with runtime async (ADR-0092; `ElarionRuntimeAsync=false` opts out); when a change touches async hot paths,
compare both models with `ELARION_BENCH_RUNTIME_ASYNC=compare` (see `tests/Elarion.Benchmarks/Program.cs`). Benchmark
methods should await the call under test the way application code does: returning a `ValueTask` straight to
BenchmarkDotNet's harness measures the harness's own continuation.
When changing the TypeScript generator, generate from a representative `rpc-schema.json` and type-check
`rpc-types.ts`, `rpc-schemas.ts`, and `rpc-client.ts` under `moduleResolution: NodeNext`.

The documentation website (the link check is a separate CI step and fails the deploy; a successful build does
not catch a broken route or anchor):

```bash
cd website && npm install && npm run build && npm run check:links
```

Other commands:

- Log entry: `python3 tools/log_add.py "**Update**: what and why, with a [link](page.md)"`
- Commit hook: `git config core.hooksPath tools/hooks` checks each commit message as it is made.

## Rules that always apply

- Keep Elarion independent of every downstream application: do not mention, depend on, or optimize for a
  consuming app by name. Application domain code, database conventions, UI frameworks, deployment quirks, and
  application-specific generators belong in consuming repositories.
- Use the terms in `GLOSSARY.md`; do not invent synonyms. A new term goes into the glossary in the same change.
- Record a design decision, its trade-offs and its rejected alternatives in `docs/decisions/`, with the `adr`
  skill. An ADR is rationale, not necessarily the current API; the published pages say what holds today.
- Every behavior change comes with a test, and every bug fix with a regression test. Documentation is part of
  the change: update the published page under `docs/` and `CHANGELOG.md` when consumers notice the change.
- Spec and code change together: a pull request that changes the behavior of a specified feature updates its
  spec, or says why not.
- Run `make check` and the relevant validation commands before you report a task as done.
- Never commit secrets. `.env` files are ignored; do not paste their contents into prompts.
- Do not use harness memory; it is off in the committed settings. See [Repository memory](#repository-memory).

**Scale positioning.** Elarion targets small-to-mid apps: roughly **1–10 nodes, vertical-first, on the
one PostgreSQL the application already runs**. Shipped defaults must work at that tier, but need not scale
past it. If a concern appears only beyond that tier, replace the relevant seam with a dedicated job engine,
broker, cache, or actor system; do not grow a default's complexity or configuration surface. Test every new
default with: “does it cover ten nodes on one Postgres?” (ADR-0025). PostgreSQL extensions are composition,
not scale-out (ADR-0056): document extension recipes, but keep packages usable without extensions.

### Architecture boundaries

- Core packages remain reusable and domain-neutral. Do not add consuming-app names, application domain
  types, host-specific UI code, deployment conventions, or application infrastructure to Elarion packages.
- `Elarion.Abstractions` holds implementation-neutral contracts: interfaces, attributes, markers, and data
  records. It has no runtime-integration dependencies. Concrete behavior, defaults, pipeline decorators, and
  telemetry live in dependency-light `Elarion` core or an opt-in sibling (ADR-0034).
- `Elarion` core remains dependency-light and transport-neutral: it depends on Abstractions and
  `Microsoft.Extensions.*` abstractions, not ASP.NET, EF, a protocol package, or a concrete third-party
  runtime provider. Heavy defaults belong in opt-in siblings; keep their seam in Abstractions and their
  decorator in core where appropriate (ADR-0017).
- Preserve the protocol/host split. Protocol packages define a format and dispatcher without binding a wire;
  host packages bind protocol behavior to ASP.NET, sockets, or another hosting environment. Hosts may depend
  on protocols, never the reverse.
- Provider-neutral cores own provider-neutral contracts. Blobs remain streaming-first and S3-wire-free;
  upload protocols are adapters over the staged-upload seam. Connections, client events, settings, migrations,
  and integration messaging expose replaceable seams rather than assuming a single provider.
- Authorization, feature gating, and request validation use the same seam/implementation shape: attribute
  and contract in Abstractions, transport-neutral decorator in core, provider default in an opt-in package.
  Swapping a provider must not change a handler attribute or DTO.
- Validation uses standard `System.ComponentModel.DataAnnotations`. Do not add an Elarion validation
  attribute or a FluentValidation dependency. Business rules belong in handlers/domain services, not a
  pre-handler validator.
- `Elarion.AspNetCore.OpenApi` is the only package that references `Microsoft.AspNetCore.OpenApi`; do not
  leak OpenAPI into the base HTTP transport or introduce an Elarion OpenAPI MSBuild/client-generator package.
  Identity's web-free model stays in `Elarion.EntityFrameworkCore.Identity`; the ASP.NET package owns only
  host wiring.
- EF packages own EF mapping, EF generation, and EF-specific runtime behavior. Provider-neutral contracts
  such as results, pagination requests, and unit-of-work seams stay outside the EF layer. NativeAOT SQL stays
  EF-free; do not blur the two host tiers.
- Design every seam for its strongest intended implementation, never for pre-1.0 compatibility or a weak
  test/single-node implementation. A weaker implementation provides the closest semantics and documents the
  difference. Prefer an optional options bag to repeatedly widening signatures.
- Decide, do not defer. When a feature is in scope and the only obstacle is that several reasonable designs
  exist, choose the simplest coherent one, implement it, and record the choice and the rejected alternatives in
  the ADR as revisable. A pre-1.0 decision can be revisited; an unshipped feature helps nobody. Defer only when
  no answer makes sense yet, or when the choice would be unsafe (a default that fails open).
- Prefer compile-time generation over runtime reflection scanning. Framework paths must preserve trimming
  and AOT friendliness; a reflection fallback is an explicit, isolated opt-in, not a quiet default.

## Commits and pull requests

- Commit messages and pull request titles follow Conventional Commits 1.0.0:
  `<type>[(scope)][!]: <description>`, for example `feat(auth): add token refresh` or
  `fix!: reject empty payloads`. Types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`,
  `ci`, `chore`, `revert`. The description is imperative, starts lowercase and has no trailing period. The
  header stays within 100 characters; a pull request title becomes the commit on a squash merge, so write it
  as the line a reader of the history needs. `release:` is the Release workflow's own type; do not use it.
- A breaking change gets `!` before the colon and a `BREAKING CHANGE:` footer that says what users must do.
- The body says why, not what; the diff shows what. Reference the spec
  (`Refs: specs/0007-export.md, FR-002`) and the issue (`Closes #12`) in footers.
- One logical change per commit: spec, test and code of one change belong together; unrelated clean-ups go in
  their own commit.
- Branches are named `<type>/<short-slug>`, for example `feat/csv-export`; create the branch before
  implementing, because the `implement` skill commits to the current branch. Never push to `main` directly,
  never force-push a shared branch.
- Agents commit only when asked (running the `implement` skill counts), open a pull request or an issue only
  when asked, and add no tool footers or co-author trailers for themselves, in commits, pull request
  descriptions and comments alike: the person who commits owns the change and must be able to explain it.
- `python3 tools/check_commits.py` checks the commits of a branch; `git config core.hooksPath tools/hooks`
  checks each commit as it is made; CI checks the title and every commit of a pull request.

Prefer stacked PRs when a change is too large for one focused review: each layer is small, single-purpose, and
green; each PR targets the preceding branch; only the bottom targets `main`; merge bottom-up.

Avoid stranding a stack:

- Use one branch per PR; never reuse a head branch.
- **“MERGED” is not “on `main`.”** Before treating work as complete, verify
  `git merge-base --is-ancestor <pr-head-sha> origin/main`, or confirm the base chain terminates at `main`.
- After a base merges, confirm every dependent PR retargeted to `main` or the next unmerged base; retarget it
  manually when necessary.
- Do not delete or reuse a base branch while an open PR still targets it.

## Workflow and where artefacts go

The flow is clarify, specify, plan, decompose, implement, verify; `docs/workflow.md` describes each step, how
much of it a change needs, and which skill does what. Skills that produce artefacts write them here, whatever
location they would otherwise use:

- Decisions and terms found while clarifying (grilling, domain modeling): `docs/decisions/` and `GLOSSARY.md`,
  in the formats those files define. Public skills name `docs/adr/` for decisions; that folder does not exist
  here and `make check` fails when it appears.
- Specs: one file per feature, `specs/NNNN-slug.md`, in the format of `specs/README.md`, written with the
  `spec` skill or any skill that produces a spec. A skill that wants to publish a spec to an issue tracker
  writes the file first and links it from the issue. Features that shipped before `specs/` existed have no
  spec and get none after the fact; a change to one gets a spec for the change.
- Implementation and testing decisions: `specs/NNNN-slug.plan.md` next to the spec, or an ADR when they are
  hard to reverse. Never in the spec.
- Tickets: in the issue tracker, each linking the spec and naming the FR numbers it covers. Local tickets in
  `.scratch/<feature>/issues/` are allowed for solo work, but git ignores them, so they are no evidence.
- Research notes (the `research` skill, or any agent summary of outside material):
  `docs/research/YYYY-MM-DD-topic.md` with `type: Research note`, listed in `docs/index.md`, citing the URLs
  they rest on. They are secondary: they synthesise, they are not sources.
- Material received from outside (notes, mails, requirements): `docs/sources/YYYY-MM-DD-topic.md` with
  `type: Source`, body as received. Files that cannot carry frontmatter (PDF, HTML, office files) stay outside
  `docs/`, in `raw/`.
- ADRs written by `domain-modeling` use its own short format; in this repository they get the format and the
  frontmatter of `docs/decisions/README.md` (`status: draft` while proposed, `stable` once accepted).
- Any `.md` page created under `docs/`, by whichever skill: frontmatter with a `type`, an entry in the index
  of its folder (`docs/index.md`, `docs/decisions/index.md`) and an entry in `docs/log.md`, in the same
  change. Skills do not know this format. `make check` fails on missing frontmatter or index entries; a
  missing log entry is caught in review.
- Any `.mdx` page created under `docs/`: `title` and `description`, and an entry in the `meta.json` of its
  folder. `make check` fails without them.
- Reviews check two axes: standards (this file, the pattern rules, ADRs, `GLOSSARY.md`) and spec (the spec
  file, its FR numbers, a test per acceptance scenario).

### Repository memory

Harness memory is off in the committed settings (`.claude/settings.json`, `.vscode/settings.json`;
[ADR-0096](docs/decisions/0096-the-repository-is-the-memory.md)). Do not use it, and do not write notes into a
harness's memory directory. Anything worth remembering goes into a tracked file, in the same change that taught it:

| What you learned | Where it goes |
| --- | --- |
| A correction or a working rule that always applies | A rule in this file |
| A rule for one kind of file | The pattern rule in `.github/instructions/` |
| A procedure | A skill in `.agents/skills/` |
| A design decision, its trade-offs, its rejected alternatives | An ADR in [`docs/decisions/`](docs/decisions/), listed in its `index.md` |
| A term and its meaning | [`GLOSSARY.md`](GLOSSARY.md) |
| What a new feature must do | A spec in [`specs/`](specs/) |
| How a feature behaves or is used | [`docs/concepts/`](docs/concepts/), [`docs/capabilities/`](docs/capabilities/), [`docs/reference/`](docs/reference/) |
| What an outside source says | A research note in [`docs/research/`](docs/research/) |
| How to build, test, or set up a machine for this repository | [`CONTRIBUTING.md`](CONTRIBUTING.md) |
| What changed for consumers | [`CHANGELOG.md`](CHANGELOG.md) |
| What changed in the knowledge pages, and why | [`docs/log.md`](docs/log.md) |

Working notes, scratch files, and session hand-overs go into `.scratch/`, which git ignores, or into the OS temp
directory — never into tracked files. Personal preferences and facts about one machine (where an SDK is installed)
stay in that person's user-level instruction file, outside the repository.

## Agent skills

### Issue tracker

GitHub issues of `swimmesberger/Elarion`, through the `gh` CLI; specs are files in `specs/` that the issue
links. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: `GLOSSARY.md` at the root, the ADRs in `docs/decisions/` (not `docs/adr/`), the published
pages under `docs/`, plus `specs/`. See `docs/agents/domain.md`.

## If a spec framework is added

One per repository. It keeps its own folder outside `docs/` (`.specify/`, `openspec/`, `.kiro/`), because every
`.md` page under `docs/` must be OKF conformant. Its rules file (constitution, config, steering) points to this
file and does not repeat it. If it wants a managed block in an agent context file, the block goes here, never
into a `CLAUDE.md` router. Its skills share `.agents/skills/`; install and update them only with its own
command. Existing specs move into its layout in one change.

## Pointers: read these when the task calls for it

- How to work on a task, how much process it needs, which skill to use: `docs/workflow.md`.
- A term is unclear, or you are about to name something: `GLOSSARY.md`.
- Designing or changing anything under `src/`, `tests/` or `samples/`: read
  `.github/instructions/framework.instructions.md` first. It holds the package layout and the framework
  invariants: modules, handlers and transports; serialization and AOT; authorization, feature gates and
  validation; events and cross-module communication; actors, connections and live updates; persistence, blobs
  and SQL; file formats, import and export; the TypeScript client and frontend contributions.
- Writing or changing a `.cs` file: `.github/instructions/csharp.instructions.md`; mechanical formatting is in
  `.editorconfig`.
- Writing or changing a generator, an analyzer or a generator test:
  `.github/instructions/generators.instructions.md`, then
  [ADR-0006](docs/decisions/0006-incremental-source-generator-conventions.md).
- Writing or changing a test or a benchmark: `.github/instructions/tests.instructions.md`.
- Adding or changing a page under `docs/`, or the website: `.github/instructions/docs.instructions.md`, then
  `docs/guide.md`.
- Adding or changing a workflow, or anything about publishing: `.github/instructions/workflows.instructions.md`;
  operating a release: [`RELEASING.md`](RELEASING.md).
- Which package to add, and what it may depend on: [`docs/reference/packages.mdx`](docs/reference/packages.mdx).
- Current behavior and public API: [`docs/concepts/`](docs/concepts/) and
  [`docs/capabilities/`](docs/capabilities/); attributes, diagnostics and configuration:
  [`docs/reference/`](docs/reference/).
- Package boundaries, why the code is shaped as it is, whether something may be added: the architecture
  boundaries above, `docs/architecture.md`, then the ADRs it cites.
- A decision seems odd, or you are about to make one that is hard to reverse: `docs/decisions/index.md` and the
  `adr` skill.
- What a feature must do: its file in `specs/`, or, for a feature older than `specs/`, its published page; how
  it is built: the `.plan.md` next to the spec.
- Build, validation and machine setup: [`CONTRIBUTING.md`](CONTRIBUTING.md).
- What happened before, or why a page says what it says: `CHANGELOG.md` for the packages, `docs/log.md` for
  the knowledge pages, then `docs/sources/`.
- Anything you learned about the project and would otherwise remember: put it in one of the files of
  [Repository memory](#repository-memory), list new pages in their index, and add a log entry in the same change.

## Where the rest lives

- Pattern rules: `.github/instructions/*.instructions.md` (source), mirrored for Claude Code in
  `.claude/rules/`. `.github/copilot-instructions.md` is a pointer for Copilot surfaces that do not read this
  file. Copilot code review reads the pattern rules but not this file, so a rule that must reach code review
  belongs in a pattern rule.
- Procedures: `.agents/skills/*/SKILL.md`. `elarion` is the skill for building on the framework; `adr`, `spec`
  and `verify` are this repository's own; the others are installed by `apm.yml` and pinned in `apm.lock.yaml`.
  Never run `apm compile`: it would generate this file, which is written by hand.
- Tool servers: none today. When one is needed it goes into `.mcp.json` under `mcpServers`, with
  `.cursor/mcp.json` as a symlink to it; `make check` requires both together.
- Tracker: GitHub issues and pull requests of `swimmesberger/Elarion`.
- Published documentation: <https://elarion.wimmesberger.dev>, deployed from `main` by `deploy-docs.yml`.
- Packages: NuGet (`Elarion.*`) and npm (`@swimmesberger/elarion-*`), published by `publish.yml`.
