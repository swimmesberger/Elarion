#!/usr/bin/env python3
"""Check commit messages and pull request titles against Conventional Commits 1.0.0 (spec 0002, ADR-0097).

Usage:
  tools/check_commits.py [RANGE]        every commit in RANGE, default <remote default branch>..HEAD,
                                        for example origin/main..HEAD (CI, before a push)
  tools/check_commits.py --title TEXT   one pull request title (CI)
  tools/check_commits.py --file PATH    one message file, as git passes it to the commit-msg hook

Merge commits are skipped. `fixup!`, `squash!` and `amend!` commits pass the hook, so they can be autosquashed,
and fail a range check, so they do not reach main. Standard library only. Exit code 1 on any finding.
"""
import re
import subprocess
import sys

# `release` is the type of the commit the Release workflow writes (`release: v1.2.3 [skip ci]`); people do not use it.
TYPES = ("feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert", "release")
HEADER = re.compile(r"^(?P<type>[A-Za-z]+)(?:\((?P<scope>[^()\s]+)\))?(?P<bang>!)?: (?P<description>\S.*)$")
# A pull request title becomes the changelog-grade subject of a squash merge here, so the limit is 100, not 72.
MAX_HEADER = 100


def problems(message: str, allow_fixup: bool = False) -> list[str]:
    """Return what is wrong with a commit message or title; empty when it conforms."""
    lines = [l for l in message.splitlines() if not l.startswith("#")]
    header = next((l for l in lines if l.strip()), "").rstrip()
    if allow_fixup and header.startswith(("fixup! ", "squash! ", "amend! ")):
        return []
    if header.startswith(("fixup! ", "squash! ", "amend! ")):
        return [f"{header!r}: squash fixup commits before merging"]
    match = HEADER.match(header)
    if not match:
        return [f"{header!r}: expected `<type>[(scope)][!]: <description>`, for example `feat(auth): add token refresh`"]
    found = []
    if match["type"] not in TYPES:
        found.append(f"{header!r}: unknown type {match['type']!r}; use one of {', '.join(TYPES)}")
    if match["description"].endswith("."):
        found.append(f"{header!r}: no trailing period in the description")
    if len(header) > MAX_HEADER:
        found.append(f"{header!r}: header is {len(header)} characters; keep it within {MAX_HEADER}")
    body = lines[lines.index(header) + 1:] if header in lines else []
    if body and body[0].strip():
        found.append(f"{header!r}: leave a blank line between header and body")
    return found


def default_base() -> str:
    """The remote's default branch (origin/HEAD), falling back to origin/main."""
    out = subprocess.run(["git", "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], capture_output=True, text=True)
    return out.stdout.strip() or "origin/main"


def commits(rev_range: str) -> list[tuple[str, str]]:
    """Return (short sha, full message) for every non-merge commit in the range, oldest first."""
    out = subprocess.run(
        ["git", "log", "--no-merges", "--reverse", "--format=%h%x00%B%x01", rev_range],
        capture_output=True, text=True, check=True,
    ).stdout
    return [tuple(entry.strip("\n").split("\0", 1)) for entry in out.split("\x01") if entry.strip()]


def main(argv: list[str]) -> int:
    if argv[:1] == ["--title"] and len(argv) == 2:
        found = problems(argv[1])
    elif argv[:1] == ["--file"] and len(argv) == 2:
        with open(argv[1], encoding="utf-8") as f:
            found = problems(f.read(), allow_fixup=True)
    elif len(argv) <= 1 and not (argv and argv[0].startswith("-")):
        try:
            found = [f"{sha} {p}" for sha, message in commits(argv[0] if argv else f"{default_base()}..HEAD") for p in problems(message)]
        except subprocess.CalledProcessError as e:
            print(e.stderr.strip() or e, file=sys.stderr)
            return 2
    else:
        print(__doc__, file=sys.stderr)
        return 2
    for f in found:
        print(f"  - {f}")
    if found:
        print("not Conventional Commits; see `Commits and pull requests` in AGENTS.md")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
