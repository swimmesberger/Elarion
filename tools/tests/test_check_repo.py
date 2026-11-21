"""Acceptance tests for spec 0001, one per scenario, named after it.

Each test builds a minimal conforming repository in a temporary folder, breaks one thing, and runs the
check. The fixture does not depend on this repository's own pages, so they can be replaced freely.
"""
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from check_repo import check  # noqa: E402

PAGE = "---\ntype: {type}\ntitle: \"{title}\"\ndescription: \"{title}.\"\nstatus: stable\n---\n# {title}\n"
ADR = "---\ntype: Architecture Decision Record\ntitle: \"ADR-{number}: One\"\nstatus: {status}\n---\n# ADR-{number}: One\n\n- Status: {word}\n- Date: 2026-01-01\n\n## Context\n"
MDX = "---\ntitle: {title}\ndescription: What {title} is.\n---\n\nText.\n"
SPEC = """---
type: Specification
status: {status}
---
# Spec 0001: example

## Intent

Something useful.

## User scenarios

### One (P1)

- Given a, when b, then c.

## Requirements

- FR-001: The system MUST do a.
- FR-002: The system MUST do b.

## Success criteria

- SC-001: Every scenario has a test.

## Out of scope

Nothing.
"""
FIXTURE = {
    "AGENTS.md": "# Example\n",
    "CLAUDE.md": "@AGENTS.md\n",
    "GLOSSARY.md": PAGE.format(type="Glossary", title="Glossary"),
    ".agents/skills/verify/SKILL.md": "---\nname: verify\ndescription: Run the checks.\n---\n# Verify\n",
    ".github/instructions/tests.instructions.md": "---\napplyTo: \"**/tests/**\"\n---\n# Tests\n",
    ".claude/rules/tests.md": "---\npaths:\n  - \"**/tests/**\"\n---\nThe rules are in `.github/instructions/tests.instructions.md`.\n",
    ".mcp.json": '{"mcpServers": {"github": {"type": "http", "url": "https://example.com/mcp/"}}}\n',
    "docs/index.md": "---\nokf_version: \"0.2\"\n---\n# Knowledge\n\n* [Guide](guide.md) - how it is kept.\n* [Decisions](decisions/index.md) - the ADRs.\n* [Log](log.md) - changes.\n",
    "docs/guide.md": PAGE.format(type="Guide", title="Guide"),
    "docs/log.md": "# Log\n\n## 2026-02-01\n\n* **Update**: [guide](guide.md)\n\n## 2026-01-01\n\n* **Creation**: [index](index.md)\n",
    "docs/decisions/index.md": "# Decisions\n\n* [ADR-0001](0001-one.md) - One\n",
    "docs/decisions/0001-one.md": ADR.format(number="0001", status="stable", word="Accepted"),
    "docs/meta.json": '{"title": "Docs", "root": true, "pages": ["index", "concepts"]}\n',
    "docs/index.mdx": MDX.format(title="Introduction"),
    "docs/concepts/meta.json": '{"title": "Concepts", "pages": ["handlers"]}\n',
    "docs/concepts/index.mdx": MDX.format(title="Concepts"),
    "docs/concepts/handlers.mdx": MDX.format(title="Handlers"),
    "specs/0001-example.md": SPEC.format(status="stable"),
}


class RepositoryCheck(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        for rel, text in FIXTURE.items():
            self.write(rel, text)
        os.symlink("../.agents/skills", self.root / ".claude" / "skills")
        (self.root / ".cursor").mkdir()
        os.symlink("../.mcp.json", self.root / ".cursor" / "mcp.json")

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, rel, text):
        path = self.root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def edit(self, rel, old, new):
        path = self.root / rel
        text = path.read_text(encoding="utf-8")
        self.assertIn(old, text)
        path.write_text(text.replace(old, new, 1), encoding="utf-8")

    def assertFinding(self, path, *words):
        findings = check(self.root)
        hits = [f for f in findings if f.startswith(path) and all(w in f for w in words)]
        self.assertTrue(hits, f"no finding for {path} with {words}; got {findings}")

    def test_a_consistent_repository_passes(self):
        self.assertEqual(check(self.root), [])

    def test_a_router_with_content_is_reported(self):
        self.write("CLAUDE.md", "@AGENTS.md\n\nAlways use tabs.\n")
        self.assertFinding("CLAUDE.md", "only `@AGENTS.md`")

    def test_an_agents_md_without_router_is_reported(self):
        self.write("app/AGENTS.md", "# app\n")
        self.assertFinding("app/AGENTS.md", "no CLAUDE.md router")

    def test_a_mirror_with_different_globs_is_reported(self):
        self.edit(".claude/rules/tests.md", "**/tests/**", "**/test/**")
        self.assertFinding(".claude/rules/tests.md", "differ from applyTo")

    def test_a_mirror_that_repeats_the_rule_is_reported(self):
        self.edit(".claude/rules/tests.md", "The rules are", "word " * 70 + "The rules are")
        self.assertFinding(".claude/rules/tests.md", "a mirror is a pointer")

    def test_a_mirror_without_source_is_reported(self):
        self.write(".claude/rules/api.md", "---\npaths:\n  - \"api/**\"\n---\nSee `.github/instructions/api.instructions.md`.\n")
        self.assertFinding(".claude/rules/api.md", "no source")

    def test_a_skill_whose_name_differs_from_its_folder_is_reported(self):
        self.edit(".agents/skills/verify/SKILL.md", "name: verify", "name: check")
        self.assertFinding(".agents/skills/verify/SKILL.md", "must equal the folder name")

    def test_a_broken_link_for_a_harness_is_reported(self):
        (self.root / ".cursor" / "mcp.json").unlink()
        self.assertFinding(".cursor/mcp.json", "missing")

    def test_an_mcp_server_without_a_target_is_reported(self):
        self.write(".mcp.json", '{"mcpServers": {"github": {"type": "http"}}}\n')
        self.assertFinding(".mcp.json", "needs a url")

    def test_a_published_page_without_title_or_description_is_reported(self):
        self.edit("docs/concepts/handlers.mdx", "description: What Handlers is.\n", "")
        self.assertFinding("docs/concepts/handlers.mdx", "`description`")

    def test_a_published_page_missing_from_the_navigation_is_reported(self):
        self.write("docs/concepts/modules.mdx", MDX.format(title="Modules"))
        self.assertFinding("docs/concepts/modules.mdx", "not listed in docs/concepts/meta.json")

    def test_a_published_page_kept_out_of_the_navigation_on_purpose_passes(self):
        self.write("docs/concepts/moved.mdx", MDX.format(title="Moved"))
        self.edit("docs/concepts/meta.json", '"handlers"', '"handlers", "!moved"')
        self.assertEqual(check(self.root), [])

    def test_a_folder_of_published_pages_missing_from_the_navigation_is_reported(self):
        self.write("docs/reference/meta.json", '{"title": "Reference", "pages": ["attributes"]}\n')
        self.write("docs/reference/attributes.mdx", MDX.format(title="Attributes"))
        self.assertFinding("docs/reference", "folder not listed in docs/meta.json")

    def test_a_navigation_entry_without_a_page_is_reported(self):
        self.edit("docs/concepts/meta.json", '"handlers"', '"handlers", "modules"')
        self.assertFinding("docs/concepts/meta.json", "'modules'")

    def test_a_published_page_needs_no_bundle_frontmatter_or_index_entry(self):
        self.write("docs/concepts/modules.mdx", MDX.format(title="Modules"))
        self.edit("docs/concepts/meta.json", '"handlers"', '"handlers", "modules", "---Advanced---", "..."')
        self.assertEqual(check(self.root), [])

    def test_an_adr_outside_docs_decisions_is_reported(self):
        self.write("docs/adr/0002-two.md", ADR.format(number="0002", status="draft", word="Proposed"))
        self.assertFinding("docs/adr", "docs/decisions")

    def test_an_adr_whose_status_disagrees_with_its_frontmatter_is_reported(self):
        self.edit("docs/decisions/0001-one.md", "- Status: Accepted", "- Status: Proposed")
        self.assertFinding("docs/decisions/0001-one.md", "status: draft")

    def test_a_superseded_adr_without_its_successor_is_reported(self):
        self.write("docs/decisions/0001-one.md", ADR.format(number="0001", status="deprecated", word="Superseded by ADR-0002"))
        self.assertFinding("docs/decisions/0001-one.md", "superseded_by")

    def test_an_adr_whose_heading_number_differs_from_its_file_is_reported(self):
        self.edit("docs/decisions/0001-one.md", "# ADR-0001: One", "# ADR-0002: One")
        self.assertFinding("docs/decisions/0001-one.md", "ADR-0001")

    def test_a_reused_adr_number_is_reported(self):
        self.write("docs/decisions/0001-other.md", ADR.format(number="0001", status="stable", word="Accepted"))
        self.edit("docs/decisions/index.md", "- One\n", "- One\n* [ADR-0001](0001-other.md) - Other\n")
        self.assertFinding("docs/decisions/0001-other.md", "already used by 0001-one.md")

    def test_a_file_in_a_build_output_or_worktree_folder_is_not_reported(self):
        self.write("src/App/bin/Release/AGENTS.md", "# copied by a build\n")
        self.write("website/node_modules/pkg/AGENTS.md", "# a dependency's\n")
        self.write(".claude/worktrees/other/AGENTS.md", "# another checkout\n")
        self.assertEqual(check(self.root), [])

    def test_an_unlisted_page_is_reported(self):
        self.write("docs/runbook.md", PAGE.format(type="Runbook", title="Runbook"))
        self.assertFinding("docs/runbook.md", "not listed in docs/index.md")

    def test_a_page_without_type_is_reported(self):
        self.edit("docs/guide.md", "type: Guide\n", "")
        self.assertFinding("docs/guide.md", "`type`")

    def test_a_page_with_an_unknown_status_is_reported(self):
        self.edit("docs/guide.md", "status: stable", "status: accepted")
        self.assertFinding("docs/guide.md", "'accepted'")

    def test_an_index_with_prose_is_reported(self):
        self.edit("docs/index.md", "# Knowledge\n", "# Knowledge\n\nThis folder holds everything.\n")
        self.assertFinding("docs/index.md", "only headings")

    def test_a_log_not_newest_first_is_reported(self):
        self.write("docs/log.md", "# Log\n\n## 2026-01-01\n\n* **Update**: [a](index.md)\n\n## 2026-02-01\n\n* **Update**: [b](index.md)\n")
        self.assertFinding("docs/log.md", "newest first")

    def test_a_spec_without_status_is_reported(self):
        self.edit("specs/0001-example.md", "status: stable\n", "")
        self.assertFinding("specs/0001-example.md", "no status")

    def test_an_approved_spec_without_a_required_section_is_reported(self):
        self.edit("specs/0001-example.md", "## Out of scope\n", "## Not covered\n")
        self.assertFinding("specs/0001-example.md", "`## Out of scope`")

    def test_an_approved_spec_with_a_duplicated_requirement_number_is_reported(self):
        self.edit("specs/0001-example.md", "- FR-002:", "- FR-001:")
        self.assertFinding("specs/0001-example.md", "FR-001")

    def test_a_draft_spec_with_an_open_clarification_passes(self):
        self.write("specs/0001-example.md", SPEC.format(status="draft").replace("Something useful.", "[NEEDS CLARIFICATION: who uses this?]"))
        self.assertEqual(check(self.root), [])

    def test_a_file_in_a_raw_or_scratch_folder_is_not_reported(self):
        self.write("raw/web/page.md", "# Stored as received\n")
        self.write(".scratch/notes.md", "# Notes\n")
        self.assertEqual(check(self.root), [])

    def test_the_check_reports_every_finding_of_a_run(self):
        self.write("CLAUDE.md", "content\n")
        self.edit("docs/guide.md", "type: Guide\n", "")
        self.assertGreaterEqual(len(check(self.root)), 2)


if __name__ == "__main__":
    unittest.main()
