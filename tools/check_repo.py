#!/usr/bin/env python3
"""Check that the repository layout has not drifted (spec 0001, ADR-0097).

Run from anywhere: python3 tools/check_repo.py [ROOT]

  1. Every AGENTS.md has a sibling CLAUDE.md whose only content is `@AGENTS.md`.
  2. Every .github/instructions/<topic>.instructions.md has a mirror .claude/rules/<topic>.md with the
     same globs and a pointer body, and every mirror has a source.
  3. .claude/skills links to .agents/skills and .cursor/mcp.json to .mcp.json, or are identical copies.
  4. Every skill has a SKILL.md whose `name` equals its folder and whose `description` is set.
  5. .mcp.json, when present, uses the `mcpServers` key and every server has a valid type and target.
  6. The `.md` pages of docs/ are an Open Knowledge Format 0.2 bundle: every page, the glossary and every spec
     carry frontmatter with a `type`, and a `status`, when set, is draft, stable or deprecated; every page is
     listed in the nearest index.md; indexes hold only headings and entries whose local links resolve; the log
     has date headings, newest first, and every entry links a page. Whether a change has a log entry is not
     checked: that needs the diff, and is left to review.
  7. The `.mdx` pages of docs/ are published by the website: every page has a `title` and a `description`,
     and is listed in the `pages` of the meta.json next to it (an index.mdx is its folder's landing page and
     needs no entry; `!name` lists a page that is deliberately kept out of the navigation); every `pages`
     entry resolves to a page or a folder.
  8. ADRs live in docs/decisions, never in docs/adr: the number in the file name is unique and equals the one
     in the `# ADR-NNNN:` heading, and the frontmatter `status` agrees with the `- Status:` line.
  9. Every spec has a status; every spec with `status: stable` (approved) has the required sections in order,
     unique FR and SC numbers and no open clarification.

Dependency, build-output, worktree, scratch and raw folders (SKIP below) are never entered.

Standard library only. Exit code 1 on any finding.
"""
import filecmp
import fnmatch
import json
import os
import re
import sys
from pathlib import Path

# Folder names that are never entered, at any depth: dependencies, caches, build output of the .NET projects
# and of the website, the worktrees a harness keeps under .claude/, scratch notes and raw files.
SKIP = {".git", "node_modules", "apm_modules", ".scratch", "raw", "bin", "obj", ".next", "out", "worktrees"}
STATUSES = ("draft", "stable", "deprecated")
SPEC_SECTIONS = ["Intent", "User scenarios", "Requirements", "Success criteria", "Out of scope"]
ENTRY = re.compile(r"^\* \[[^\]]+\]\(([^)]+)\) - \S")
DAY = re.compile(r"^## (\d{4}-\d{2}-\d{2})$", re.M)
# meta.json `pages` entries that name no file: the rest operator, a separator, a link, an extracted folder.
META_SYNTAX = re.compile(r"^(\.\.\..*|---.*---|\[.*\]\(.*\))$")
ADR_STATUS = {"proposed": "draft", "accepted": "stable", "superseded": "deprecated", "deprecated": "deprecated"}


def frontmatter(path: Path) -> tuple[dict[str, object], str]:
    """Parse the YAML subset used here: scalars, flow maps kept as strings, dash lists. Returns (fields, body)."""
    text = path.read_text(encoding="utf-8")
    if not text.startswith("---\n"):
        return {}, text
    head, _, body = text[4:].partition("\n---\n")
    fields: dict[str, object] = {}
    key = None
    for line in head.splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if line.startswith("  - ") and key:
            items = fields.setdefault(key, [])
            if isinstance(items, list):
                items.append(line[4:].strip().strip("\"'"))
        else:
            key, _, value = line.partition(":")
            key, value = key.strip(), value.split(" #")[0].strip()
            fields[key] = value.strip("\"'") if value else []
    return fields, body


def walk(root: Path, pattern: str):
    """Yield the files under root whose name matches pattern, without entering a SKIP folder or a symlink."""
    for folder, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in SKIP)
        for name in sorted(files):
            if fnmatch.fnmatchcase(name, pattern):
                yield Path(folder) / name


def globs(value: object) -> set[str]:
    if isinstance(value, list):
        return {g.strip() for g in value}
    return {g.strip() for g in str(value).split(",") if g.strip()}


def same_tree(a: Path, b: Path) -> bool:
    if a.is_file() and b.is_file():
        return filecmp.cmp(a, b, shallow=False)
    if a.is_dir() and b.is_dir():
        cmp = filecmp.dircmp(a, b)
        return not (cmp.left_only or cmp.right_only or cmp.diff_files)
    return False


class Checker:
    def __init__(self, root: Path):
        self.root = root.resolve()
        self.findings: list[str] = []

    def rel(self, path: Path) -> str:
        return path.relative_to(self.root).as_posix()

    def fail(self, path: Path, msg: str) -> None:
        self.findings.append(f"{self.rel(path)}: {msg}")

    def routers(self) -> None:
        for agents in walk(self.root, "AGENTS.md"):
            claude = agents.with_name("CLAUDE.md")
            if not claude.exists():
                self.fail(agents, "no CLAUDE.md router next to it; create one containing only `@AGENTS.md`")
                continue
            lines = [l.strip() for l in claude.read_text(encoding="utf-8").splitlines() if l.strip()]
            if lines != ["@AGENTS.md"]:
                self.fail(claude, "must contain only `@AGENTS.md`; content of its own stops Claude Code from reading AGENTS.md. Move it into AGENTS.md")

    def mirrors(self) -> None:
        src_dir = self.root / ".github" / "instructions"
        mirror_dir = self.root / ".claude" / "rules"
        for src in sorted(src_dir.glob("*.instructions.md")) if src_dir.is_dir() else []:
            topic = src.name.removesuffix(".instructions.md")
            mirror = mirror_dir / f"{topic}.md"
            if not mirror.exists():
                self.fail(src, f"no mirror at .claude/rules/{topic}.md")
                continue
            sf, _ = frontmatter(src)
            mf, body = frontmatter(mirror)
            if globs(sf.get("applyTo", "")) != globs(mf.get("paths", "")):
                self.fail(mirror, f"paths {sorted(globs(mf.get('paths', '')))} differ from applyTo {sorted(globs(sf.get('applyTo', '')))} in {src.name}")
            if src.name not in body:
                self.fail(mirror, f"body must point at {src.name} instead of repeating it")
            elif len(body.split()) > 60:
                self.fail(mirror, f"body has {len(body.split())} words; a mirror is a pointer, not a copy")
        for mirror in sorted(mirror_dir.glob("*.md")) if mirror_dir.is_dir() else []:
            if not (src_dir / f"{mirror.stem}.instructions.md").exists():
                self.fail(mirror, "no source in .github/instructions; write the rule there and mirror it here")

    def link(self, link: Path, target: Path) -> None:
        if link.is_symlink():
            if link.resolve() != target.resolve():
                self.fail(link, f"symlink points to {link.resolve()}, expected {self.rel(target)}")
        elif link.exists():
            if not same_tree(link, target):
                self.fail(link, f"is a copy that differs from {self.rel(target)}; replace it with a symlink")
        else:
            self.findings.append(f"{self.rel(link)}: missing; create a symlink to {self.rel(target)}")

    def skills(self) -> None:
        skills = self.root / ".agents" / "skills"
        if not skills.is_dir():
            self.findings.append(".agents/skills: missing")
            return
        self.link(self.root / ".claude" / "skills", skills)
        for folder in sorted(p for p in skills.iterdir() if p.is_dir()):
            skill = folder / "SKILL.md"
            if not skill.exists():
                self.fail(folder, "no SKILL.md")
                continue
            f, _ = frontmatter(skill)
            if f.get("name") != folder.name:
                self.fail(skill, f"name {f.get('name')!r} must equal the folder name {folder.name!r}")
            if not re.fullmatch(r"[a-z0-9]+(-[a-z0-9]+)*", folder.name):
                self.fail(folder, "folder name must be lowercase letters, digits and single hyphens")
            if not str(f.get("description", "")).strip():
                self.fail(skill, "description is required; it is how a harness decides to load the skill")

    def mcp(self) -> None:
        mcp = self.root / ".mcp.json"
        if not mcp.exists():
            return
        try:
            servers = json.loads(mcp.read_text(encoding="utf-8")).get("mcpServers")
        except json.JSONDecodeError as e:
            self.fail(mcp, f"invalid JSON ({e})")
            return
        if not isinstance(servers, dict):
            self.fail(mcp, "top-level key must be `mcpServers`; VS Code's `servers` is not read by Claude Code or Copilot CLI")
            return
        for name, server in servers.items():
            kind = server.get("type")
            if kind not in {"stdio", "http", "sse"}:
                self.fail(mcp, f"server {name}: type must be stdio, http or sse (found {kind!r})")
            elif kind == "stdio" and not server.get("command"):
                self.fail(mcp, f"server {name}: a stdio server needs a command")
            elif kind != "stdio" and not server.get("url"):
                self.fail(mcp, f"server {name}: an {kind} server needs a url")
        self.link(self.root / ".cursor" / "mcp.json", mcp)

    def bundle(self) -> None:
        docs = self.root / "docs"
        if not (docs / "index.md").exists():
            self.findings.append("docs/index.md: missing; it is the map of the knowledge bundle")
            return
        pages = [p for p in walk(docs, "*.md") if p.name not in {"index.md", "log.md"}]
        for page in pages + list(walk(self.root / "specs", "*.md")) + [self.root / "GLOSSARY.md"]:
            if not page.exists():
                continue
            fm, _ = frontmatter(page)
            if not str(fm.get("type", "")).strip():
                self.fail(page, "no OKF frontmatter with a `type`; see docs/guide.md")
            if "status" in fm and fm["status"] not in STATUSES:
                self.fail(page, f"status {fm['status']!r} is not one of {', '.join(STATUSES)} (OKF 0.2)")
        for index in walk(docs, "index.md"):
            self.index(index, docs)
        for page in pages:
            index = next(d / "index.md" for d in page.parents if (d / "index.md").exists())
            target = page.relative_to(index.parent).as_posix()
            if f"]({target})" not in index.read_text(encoding="utf-8"):
                self.fail(page, f"not listed in {self.rel(index)}; a page the index does not list does not exist for an agent")
        self.log(docs / "log.md")

    def index(self, index: Path, docs: Path) -> None:
        fm, body = frontmatter(index)
        allowed = {"okf_version"} if index.parent == docs else set()
        if set(fm) - allowed:
            self.fail(index, f"an index carries no frontmatter{' except okf_version' if allowed else ''} (OKF 0.2 section 8)")
        for line in body.splitlines():
            if not line.strip() or line.startswith("#"):
                continue
            entry = ENTRY.match(line)
            if not entry:
                self.fail(index, f"holds only headings and `* [Title](path) - description` entries (OKF 0.2 section 8), found: {line[:50]}")
            elif "://" not in entry[1] and not (index.parent / entry[1].split("#")[0]).exists():
                self.fail(index, f"entry links to {entry[1]}, which does not exist")

    def log(self, log: Path) -> None:
        if not log.exists():
            self.findings.append("docs/log.md: missing")
            return
        text = log.read_text(encoding="utf-8")
        days = DAY.findall(text)
        if not days:
            self.fail(log, "no `## YYYY-MM-DD` date headings")
        elif days != sorted(days, reverse=True) or len(days) != len(set(days)):
            self.fail(log, "date headings must be unique and newest first (OKF 0.2 section 9); add entries with tools/log_add.py")
        unlinked = [l for l in text.splitlines() if l.startswith("* ") and "](" not in l]
        if unlinked:
            self.fail(log, f"{len(unlinked)} entries link no page, first: {unlinked[0][:60]}")

    def site(self) -> None:
        docs = self.root / "docs"
        metas: dict[Path, list[str] | None] = {}

        def listed(folder: Path) -> list[str] | None:
            if folder not in metas:
                meta = folder / "meta.json"
                try:
                    pages = json.loads(meta.read_text(encoding="utf-8")).get("pages") if meta.exists() else None
                except json.JSONDecodeError as e:
                    self.fail(meta, f"invalid JSON ({e})")
                    pages = None
                metas[folder] = [str(p) for p in pages] if isinstance(pages, list) else None
            return metas[folder]

        def entry(folder: Path, name: str, what: str, path: Path) -> None:
            pages = listed(folder)
            if pages is None:
                self.fail(path, f"{self.rel(folder / 'meta.json')} is missing or has no `pages`; the website's navigation is built from it")
            elif name not in pages and f"!{name}" not in pages and "..." not in pages:
                self.fail(path, f"{what} not listed in {self.rel(folder / 'meta.json')}; add \"{name}\" to `pages`, or \"!{name}\" to keep it out of the navigation on purpose")

        folders = set()
        for page in walk(docs, "*.mdx"):
            fm, _ = frontmatter(page)
            for key in ("title", "description"):
                if not str(fm.get(key, "")).strip():
                    self.fail(page, f"no `{key}` in its frontmatter; the website and its search need it")
            folders.update(d for d in page.parents if docs in d.parents)
            if page.name != "index.mdx" or page.parent == docs:
                entry(page.parent, page.stem, "page", page)
        for folder in sorted(folders):
            entry(folder.parent, folder.name, "folder", folder)
        for folder, pages in sorted(metas.items()):
            for name in pages or []:
                target = name.removeprefix("!")
                if not META_SYNTAX.match(name) and not (folder / f"{target}.mdx").exists() and not (folder / target).is_dir():
                    self.fail(folder / "meta.json", f"`pages` lists {name!r}, which is neither a page nor a folder next to it")

    def decisions(self) -> None:
        if (self.root / "docs" / "adr").exists():
            self.findings.append("docs/adr: ADRs live in docs/decisions; move the record there and give it the format of docs/decisions/README.md")
        folder = self.root / "docs" / "decisions"
        seen: dict[str, Path] = {}
        for adr in sorted(folder.glob("[0-9][0-9][0-9][0-9]-*.md")) if folder.is_dir() else []:
            number = adr.name[:4]
            if number in seen:
                self.fail(adr, f"number {number} is already used by {seen[number].name}; take the next free number and never reuse one")
            seen.setdefault(number, adr)
            fm, body = frontmatter(adr)
            heading = re.search(r"^# ADR-(\d{4}):", body, flags=re.M)
            if not heading or heading[1] != number:
                self.fail(adr, f"heading must be `# ADR-{number}: <title>`, with the number of the file name")
            word = re.search(r"^- Status: (\w+)", body, flags=re.M)
            expected = ADR_STATUS.get(word[1].lower()) if word else None
            if expected is None:
                self.fail(adr, "no `- Status:` line starting with Proposed, Accepted, Superseded or Deprecated (docs/decisions/README.md)")
            elif fm.get("status") != expected:
                self.fail(adr, f"`- Status: {word[1]}` needs frontmatter `status: {expected}`, found {fm.get('status')!r}")
            elif word[1].lower() == "superseded" and not fm.get("superseded_by"):
                self.fail(adr, "a superseded ADR names its successor in frontmatter: `superseded_by: NNNN`")

    def specs(self) -> None:
        for spec in walk(self.root / "specs", "[0-9][0-9][0-9][0-9]-*.md"):
            if spec.name.endswith(".plan.md"):
                continue
            fm, body = frontmatter(spec)
            if "status" not in fm:
                self.fail(spec, "no status; a spec is `draft` until it is approved, then `stable`")
            if fm.get("status") != "stable":
                continue
            headings = re.findall(r"^## (.+?)\s*$", body, flags=re.M)
            present = [h for h in headings if h in SPEC_SECTIONS]
            for section in SPEC_SECTIONS:
                if section not in headings:
                    self.fail(spec, f"missing section `## {section}` (specs/README.md)")
            if present != [s for s in SPEC_SECTIONS if s in present]:
                self.fail(spec, f"sections out of order; expected {', '.join(SPEC_SECTIONS)}")
            for prefix in ("FR", "SC"):
                ids = re.findall(rf"^\s*[-*] ({prefix}-\d{{3}}):", body, flags=re.M)
                dupes = sorted({i for i in ids if ids.count(i) > 1})
                if dupes:
                    self.fail(spec, f"duplicated {prefix} numbers {', '.join(dupes)}; number them uniquely and never reuse one")
            if "[NEEDS CLARIFICATION" in body:
                self.fail(spec, "an approved spec has an open [NEEDS CLARIFICATION]; resolve it or set status: draft")

    def run(self) -> list[str]:
        for step in (self.routers, self.mirrors, self.skills, self.mcp, self.bundle, self.site, self.decisions, self.specs):
            step()
        return self.findings


def check(root: Path) -> list[str]:
    """Return every finding for the repository at root; empty when it is consistent."""
    return Checker(root).run()


def main() -> int:
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
    findings = check(root)
    if findings:
        print(f"repository drift, {len(findings)} findings:")
        for f in findings:
            print(f"  - {f}")
        return 1
    print("repository consistent")
    return 0


if __name__ == "__main__":
    sys.exit(main())
