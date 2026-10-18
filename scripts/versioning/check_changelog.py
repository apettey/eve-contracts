#!/usr/bin/env python3
"""Check that a PR adds a release-log entry (format: .claude/skills/ship-change/references/formats.md).

  python3 scripts/versioning/check_changelog.py [--base REV] [--head REV] [--author LOGIN]

Passes when HEAD has a `changelog/YYYY-MM-DD-<short-name>/README.md` (real date, lower-case
hyphenated name) whose folder does not exist at BASE. In PR CI the checkout is the PR merged into
its base, so the defaults HEAD^1..HEAD are exactly what the squash commit will add. PRs by
Dependabot (`--author dependabot[bot]`) are exempt: they can't write an entry.

Exit code 0 = OK, 1 = no new entry (reasons on stderr). Standard library only.
"""

from __future__ import annotations

import argparse
import datetime
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bump import git  # noqa: E402  (same directory)

EXEMPT_AUTHORS = {"dependabot[bot]"}
ENTRY_RE = re.compile(r"^changelog/((\d{4}-\d{2}-\d{2})-[a-z0-9]+(?:-[a-z0-9]+)*)/README\.md$")
FOLDER_RE = re.compile(r"^changelog/([^/]+)/")


def folders(paths: list[str]) -> set[str]:
    return {m.group(1) for p in paths if (m := FOLDER_RE.match(p))}


def check(base_paths: list[str], head_paths: list[str], author: str = "") -> list[str]:
    """Errors (empty = OK) for the files under changelog/ at the base and head revisions."""
    if author in EXEMPT_AUTHORS:
        return []
    old = folders(base_paths)
    new = sorted(folders(head_paths) - old)
    for p in head_paths:
        m = ENTRY_RE.match(p)
        if m and m.group(1) in new and _real_date(m.group(2)):
            return []

    errors = ["no new release-log entry: add changelog/YYYY-MM-DD-<short-name>/README.md "
              "(see .claude/skills/ship-change/references/formats.md)"]
    for f in new:
        readme = f"changelog/{f}/README.md"
        m = ENTRY_RE.match(readme)
        if not m:
            errors.append(f"changelog/{f}/: name must be YYYY-MM-DD-<short-name> (lower-case, hyphenated)")
        elif not _real_date(m.group(2)):
            errors.append(f"changelog/{f}/: {m.group(2)} is not a real date")
        elif readme not in head_paths:
            errors.append(f"changelog/{f}/: has no README.md")
    return errors


def _real_date(d: str) -> bool:
    try:
        datetime.date.fromisoformat(d)
        return True
    except ValueError:
        return False


def tree_paths(rev: str) -> list[str]:
    return [p for p in git("ls-tree", "-r", "--name-only", rev, "--", "changelog/").splitlines() if p]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", default="HEAD^1", help="the PR's base (default: HEAD^1)")
    ap.add_argument("--head", default="HEAD", help="the PR merged into its base (default: HEAD)")
    ap.add_argument("--author", default="", help="PR author login; dependabot[bot] is exempt")
    a = ap.parse_args(argv)

    if a.author in EXEMPT_AUTHORS:
        print(f"PR by {a.author}: exempt from the release-log entry")
        return 0
    try:
        errors = check(tree_paths(a.base), tree_paths(a.head), a.author)
    except RuntimeError as e:
        print(e, file=sys.stderr)
        return 1
    for e in errors:
        print(e, file=sys.stderr)
    if not errors:
        print("release-log entry: OK")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
