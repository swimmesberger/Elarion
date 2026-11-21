"""Acceptance tests for spec 0002, one per scenario, named after it."""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO / "tools"))

from check_commits import problems  # noqa: E402

SCRIPT = REPO / "tools" / "check_commits.py"


def git(cwd, *args):
    return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, check=True).stdout


class CommitCheck(unittest.TestCase):
    def test_a_conventional_message_passes(self):
        for message in ("feat(auth): add token refresh", "fix!: reject empty payloads", "docs: explain the log\n\nWhy it matters.\n\nRefs: specs/0001-repository-check.md"):
            self.assertEqual(problems(message), [], message)

    def test_a_message_without_a_type_is_reported(self):
        self.assertTrue(problems("Added token refresh"))

    def test_an_unknown_type_is_reported(self):
        self.assertIn("unknown type", problems("feature: add token refresh")[0])

    def test_the_release_workflow_commit_passes(self):
        self.assertEqual(problems("release: v0.4.0 [skip ci]"), [])

    def test_a_trailing_period_is_reported(self):
        self.assertIn("trailing period", problems("fix: handle empty payload.")[0])

    def test_an_overlong_header_is_reported(self):
        self.assertIn("within 100", problems("feat: " + "x" * 100)[0])
        self.assertEqual(problems("feat: " + "x" * 94), [])

    def test_a_body_without_blank_line_is_reported(self):
        self.assertIn("blank line", problems("feat: add export\nwith csv")[0])

    def test_a_fixup_commit_passes_the_hook_and_fails_the_branch_check(self):
        self.assertEqual(problems("fixup! feat: add export", allow_fixup=True), [])
        self.assertTrue(problems("fixup! feat: add export"))

    def test_a_branch_reports_only_its_non_conforming_commits_and_skips_merges(self):
        with tempfile.TemporaryDirectory() as tmp:
            git(tmp, "init", "-q", "-b", "main")
            git(tmp, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "Initial import")
            git(tmp, "switch", "-q", "-c", "feat/x")
            git(tmp, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "feat: add x")
            git(tmp, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "wip")
            git(tmp, "switch", "-q", "main")
            git(tmp, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "fix: y")
            git(tmp, "switch", "-q", "feat/x")
            git(tmp, "-c", "user.name=t", "-c", "user.email=t@example.com", "merge", "-q", "--no-edit", "main")
            run = subprocess.run([sys.executable, str(SCRIPT), "main..HEAD"], cwd=tmp, capture_output=True, text=True)
            self.assertEqual(run.returncode, 1)
            self.assertIn("'wip'", run.stdout)
            self.assertNotIn("feat: add x", run.stdout)
            self.assertNotIn("Merge", run.stdout)
            self.assertNotIn("Initial import", run.stdout)


if __name__ == "__main__":
    unittest.main()
