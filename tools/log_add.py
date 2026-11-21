#!/usr/bin/env python3
"""Add one entry to an OKF log.md, newest first, as OKF 0.2 section 9 requires.

Usage: tools/log_add.py [--log PATH] "**Update**: what changed and why, with [links](page.md)"

The entry goes to the top of today's `## YYYY-MM-DD` section; the section is created above all
older ones when the day has none yet. PATH defaults to docs/log.md. Standard library only.
"""
import datetime, re, sys
from pathlib import Path


def add(log, entry, day=None):
    day = day or datetime.date.today().isoformat()
    entry = entry.strip()
    if not entry.startswith("* "):
        entry = "* " + entry
    lines = log.read_text(encoding="utf-8").split("\n")
    first = next((i for i, l in enumerate(lines) if re.match(r"^## \d{4}-\d{2}-\d{2}$", l)), None)
    if first is not None and lines[first] == f"## {day}":
        at = first + 1
        while at < len(lines) and lines[at].strip() == "":
            at += 1
        lines[at:at] = [entry]
    else:
        at = first if first is not None else len(lines)
        lines[at:at] = [f"## {day}", "", entry, ""]
    log.write_text("\n".join(lines).rstrip("\n") + "\n", encoding="utf-8")


def main():
    args = sys.argv[1:]
    log = Path("docs/log.md")
    if args[:1] == ["--log"]:
        log, args = Path(args[1]), args[2:]
    if len(args) != 1:
        sys.exit(__doc__)
    add(log, args[0])


if __name__ == "__main__":
    main()
