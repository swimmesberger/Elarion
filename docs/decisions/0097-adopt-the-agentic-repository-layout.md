---
type: Architecture Decision Record
title: "ADR-0097: The repository follows the agentic repository template's layout, with docs/ shared between the knowledge bundle and the website"
description: "The repository follows the agentic repository template's layout, with docs/ shared between the knowledge bundle and the website."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: stable
---
# ADR-0097: The repository follows the agentic repository template's layout, with docs/ shared between the knowledge bundle and the website

- Status: Accepted
- Date: 2026-11-21
- Related: [ADR-0096](0096-the-repository-is-the-memory.md) (the memory rule, which took the template's routing
  and left its layout for this decision).
- References: the [agentic repository template](https://github.com/swimmesberger/agentic-repo-template) at
  commit `f7785cc`, whose ADRs 0001 to 0008 hold the reasoning for what is taken over unchanged; the
  [Open Knowledge Format 0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md).

## Context

ADR-0096 made the repository the memory: what an agent or a person learns goes into a tracked file. It did
not say how those files are laid out, and the layout it found had gaps. There was no glossary, so terms such
as *seam* or *feature module* were defined only by use. There was no place for what a feature must do before
it is built, no log, no index an agent could read to find the pages that exist, and nothing checked that the
instruction files agree with each other. The `elarion` skill existed twice and the two copies had drifted
apart.

The template answers each of these, and its layout is what public skills and harnesses expect. Three things
in Elarion meet it head-on:

- **`docs/` is published.** It is the content source of the documentation website. The template makes `docs/`
  an Open Knowledge Format bundle in which every page has frontmatter with a `type`, is listed in an index,
  and files that cannot carry frontmatter live elsewhere.
- **The ADRs are in `docs/decisions/`.** There are 96 of them. Published pages, `AGENTS.md`, the changelog, the
  website's own components, and XML documentation and READMEs inside released packages link them by absolute
  GitHub URL at that path. The template and the public domain-modeling skills use `docs/adr/`.
- **The repository builds with `dotnet` and `npm`.** The template's checks are Python.

## Decision

Elarion takes the template's layout. Each element is adopted as it is, adapted, or left out; an adaptation is
the simplest arrangement in which the template's rule and Elarion's existing constraint both hold, and each is
revisable.

### 1. `docs/`: one folder, two kinds of pages — adapted

The file extension says which kind a page is.

- A **`.md`** page is repository knowledge and is never published. These pages are the bundle: OKF frontmatter
  with a `type`, an entry in the nearest `index.md`, a `docs/log.md` entry for a change. OKF defines a bundle as
  a tree of `.md` files and states its conformance in terms of `.md` files only, so this is the bundle in the
  strict sense, not a carve-out.
- A **`.mdx`** page is published by the website (`website/source.config.ts` reads `**/*.mdx` and nothing
  else). It keeps the website's format: `title` and `description`, and an entry in the `meta.json` of its
  folder, which is the website's index. Routes and content are unchanged.
- `docs/index.md` is the one map: it lists the knowledge pages, and under "Published documentation" every
  top-level section of the website, from where `meta.json` leads on.
- `docs/public/` holds the website's static assets and stays. The template's `raw/` is for knowledge that
  cannot carry frontmatter (a PDF, an office file); an icon is not knowledge.
- The template's pages exist with the content of this repository: `docs/guide.md`, `docs/workflow.md`,
  `docs/architecture.md`, `docs/log.md`, `docs/agents/`, `docs/research/`, `docs/sources/`.
- `docs/log.md` records changes to the knowledge pages. Changes to published pages are recorded where
  consumers read them, in `CHANGELOG.md`.

`tools/check_repo.py` checks both kinds against their own rules. The template's checker reads `*.md` only and
demanded nothing of the `.mdx` pages or of `docs/public/`; it now also enforces what `AGENTS.md` already
required of a published page and nothing checked: a title, a description and a navigation entry. One page,
`docs/concepts/transports.mdx`, is a compatibility route kept out of the navigation on purpose; its
`meta.json` now says so with the website's own exclusion syntax (`"!transports"`), which changes nothing on
the site.

### 2. ADRs stay in `docs/decisions/` and get the bundle's envelope — adapted

- **The folder does not move.** No link breaks, and no number changes.
- Every ADR got OKF frontmatter: `type`, `title`, `description` and `status`, derived from its heading and its
  `- Status:` line (`Proposed` is `draft`, `Accepted` is `stable`, `Superseded` is `deprecated` with
  `superseded_by`). No `generated` field was invented for records whose author was not recorded. The body of
  no ADR changed, apart from four relative links to the research notes that moved.
- The list of records moved from `README.md` to `index.md`, in the OKF index form. `README.md` is now the
  format: when to write an ADR, the Nygard format Elarion uses (fuller than the template's), the statuses.
- The checker verifies what the two status fields make possible to get wrong: that they agree, that a
  superseded ADR names its successor, that the heading carries the number of the file, and that no number is
  used twice. It fails when `docs/adr/` exists, because that is where a public skill would write.
  `docs/agents/domain.md` and `AGENTS.md` tell the skills where the ADRs are.
- The three research notes under `docs/decisions/research/` moved to `docs/research/`, named by date as the
  template names them.
- The template's ADRs 0001 to 0008 are not copied in and renumbered. This record and ADR-0096 state what
  Elarion decided; the template's records hold the reasoning for the parts taken unchanged and are linked.

### 3. `GLOSSARY.md` — adopted

At the repository root, where the domain-modeling skills look, in their format. It holds the terms
`AGENTS.md`, the pattern rules and the published pages already use with a fixed meaning, each with the words
to avoid, and nothing else.

### 4. `specs/` — adopted, with a rule for what already ships

The format of `specs/README.md` is the template's. Three additions:

- Features that shipped before `specs/` existed get no spec after the fact. Their description of record is the
  published page and the ADRs it cites. A change to such a feature gets a spec for the change.
- In a framework the user is a developer, so a spec names the public surface (an attribute, a package, a
  diagnostic id, a wire shape) and nothing behind it.
- Specs 0001 and 0002 are the specs of the two check tools, extended for what Elarion adds, and serve as the
  worked examples. They are `draft` until a person approves them. Feature specs start at 0003.

### 5. `AGENTS.md` — restructured; scoped rules move to pattern rules

The root file has the template's sections (Layout, Commands, Rules that always apply, Commits and pull
requests, Workflow and where artefacts go, Agent skills, Pointers, Where the rest lives). It keeps what
applies to every task, including the architecture boundaries and the scale positioning. No rule was dropped:
the rule sets that govern one kind of file moved, word for word, into pattern rules.

| Pattern rule | Applies to | Holds |
| --- | --- | --- |
| `csharp` | `**/*.cs` | C# coding standards |
| `framework` | `src/**`, `tests/**`, `samples/**` | Package layout and the framework invariants |
| `generators` | `src/*.Generators/**`, `tests/Elarion.Tests/Generators/**` | Source generator conventions |
| `tests` | `tests/**` | Testing |
| `docs` | `docs/**`, `website/**` | The two kinds of pages and the website |
| `workflows` | `.github/workflows/**` | Action versions and publishing |

`.github/instructions/<topic>.instructions.md` is the source of each, and `.claude/rules/<topic>.md` mirrors
it for Claude Code with the same globs and one pointer sentence. This reverses the earlier arrangement, in
which `csharp.instructions.md` pointed back at a section of `AGENTS.md`: Copilot code review reads the
pattern rules and not `AGENTS.md`, so a rule that must reach review has to live there. `AGENTS.md` points to
each rule by task for harnesses that read neither mechanism.

### 6. Skills — adopted

`.claude/skills` is a symlink to `.agents/skills`, and the two copies of the `elarion` skill were merged into
one. The engineering skills of `mattpocock/skills` are installed by the Agent Package Manager from `apm.yml`,
at the commit the template pins, and committed with `apm.lock.yaml`; they are byte-identical to the
template's. All thirteen are taken, because `docs/workflow.md` assigns one to each step of the process and
none is specific to an application. `adr`, `spec` and `verify` are this repository's own and are written for
its paths and formats.

### 7. Tooling — adopted in Python, with four changes

`tools/check_repo.py`, `tools/check_commits.py`, `tools/log_add.py`, the commit-msg hook, their tests and the
`Makefile` come from the template. They stay Python: they need the standard library only, nothing in the
package build depends on them, and CI runs them for everyone.

- The repository check finds files with a walk that prunes dependency, build-output and worktree folders
  before entering them. The template's recursive glob would read `website/node_modules` and every `bin/` and
  `obj/` first.
- It checks the published pages and the decision records, as sections 1 and 2 describe.
- The commit check allows a header of **100** characters, not 72. Elarion's pull request titles are the
  changelog-grade subject of a squash merge: of the last 273 subjects on `main`, 143 are longer than 72
  characters and 35 longer than 100.
- It accepts the type `release`, which the Release workflow uses for the commit that publishes a version.

`make check` runs the tools' tests and the repository check. The framework's build and tests stay `dotnet` and
`npm` commands, because they take minutes and need the SDK and Docker. Two workflows run the checks next to
`ci.yml`: `check.yml` on pushes to `main` and on every pull request, `commits.yml` on the title and the
commits of every pull request. Both run for pull requests that target another branch, because in a stack only
the bottom one targets `main`.

### 8. The rest

- **Adopted:** the two `chat.use…AgentsMdFile` settings in `.vscode/settings.json`; the `.gitignore` entries
  for the APM cache and Python bytecode.
- **Adapted:** the pull request template. The existing `.github/PULL_REQUEST_TEMPLATE.md` keeps its name and
  its checklist and gains the template's "Spec or issue" and "Decisions" sections and its three checklist
  items.
- **Left out:** `.mcp.json` and the `.cursor/mcp.json` symlink. The template ships them with a GitHub server;
  this repository reaches GitHub through `gh`, as `docs/agents/issue-tracker.md` prescribes, and needs no tool
  server today. `AGENTS.md` says where one goes, and the checker enforces the pair as soon as the file exists.
- **Left out:** the template's `README.md` map. Elarion's `README.md` presents the framework to its users;
  the map of the repository is the "Layout" section of `AGENTS.md`.
- **Left out:** nested `AGENTS.md` files. See the rejected alternatives.

## Consequences

- An agent finds everything from two files: `AGENTS.md` and `docs/index.md`. A page that is in neither index
  fails the check.
- A branch that adds an ADR in the old shape fails the check after this change is merged: the record needs
  the frontmatter envelope and an entry in `docs/decisions/index.md`. ADR-0095 was merged in the old shape
  while this was written and got both with this change.
- An ADR now states its status twice, in the `- Status:` line and in the frontmatter. The check keeps the two
  in agreement.
- Contributors need Python 3.10 or newer to run the layout check locally; the framework's own build and tests
  do not.
- A pull request title longer than 100 characters, or a commit that is not a Conventional Commit, fails CI.
- Rules that used to load with every session now load when a matching file is touched. A harness that reads
  neither pattern rules nor mirrors depends on following the pointer in `AGENTS.md`.
- Windows checkouts need symlink support for `.claude/skills`, or an identical copy of the folder, which the
  check accepts.
- Not reachable from a file: the commit hook is enabled per clone (`git config core.hooksPath tools/hooks`);
  making `Layout` and `Conventional Commits` required checks is a repository setting; and a harness memory
  that lives on a server is switched off in the hosting service's settings (ADR-0096).

## Rejected alternatives

- **Moving the ADRs to `docs/adr/`.** It would make the public skills work without being told, and it would
  break every link that cannot be rewritten: GitHub does not redirect a moved file, and the old path is in
  the documentation and READMEs of packages already released. It would also strand any open branch that adds
  a record at the old path.
- **A `docs/adr` symlink to `decisions`.** GitHub does not resolve a URL through a symlinked directory, so
  links written by the second path would be dead; and the fallback where symlinks are unavailable would be a
  copy of a hundred files.
- **A separate folder for the bundle, leaving `docs/` to the website.** Skills and the template's pointers
  look in `docs/`, and the ADRs are already there.
- **Making the published pages OKF concepts** (a `type` on each of the 104 pages and an `index.md` entry next
  to each `meta.json` entry). OKF concepts are `.md` files, so the pages would gain no standing in the format,
  and every page would be indexed twice.
- **Nested `AGENTS.md` files in `src/` and `tests/`.** The template's answer for a root file that grows too
  long. The rule sets here are shaped by file kind, not by folder (C# files and generator projects are
  everywhere under `src/`), Copilot code review reads pattern rules and not nested files, and one mechanism is
  easier to keep than two. A pattern rule becomes a nested `AGENTS.md` with one move if a harness that reads
  only those matters more later.
- **Porting the checks to Node or C#.** Node is already required for the npm packages, so a port would keep
  the repository at two toolchains. It would also fork tested code that the template maintains, for no change
  in behavior. Revisit if Python turns out to keep contributors from running the check.
- **The template's 72-character header limit.** It would reject more than half of the titles this repository
  writes today.
- **A `.mcp.json` with the template's GitHub server.** Every session would be asked to approve and
  authenticate a server whose work `gh` already does.
