#!/usr/bin/env python3
"""Validate an announcement file (whats-new/features.json; format: each repo's
.claude/skills/ship-change/references/formats.md). Shared by eve-contracts, hangarscope and
multi-box through the composite action `.github/actions/validate-features`.

  python3 scripts/versioning/validate_features.py [path] [--repo-root DIR] [--image-prefix P]
                                                  [--base-file F | --base-ref REF] [--pr N]

Checks: valid JSON; {"features": [...]}; each entry has an integer `pr` (unique), a real
`date` (YYYY-MM-DD), a non-empty `title` and plain-text `description`; optional `images` is a
list of 1-2 {src, caption} whose src starts with --image-prefix and exists under --repo-root
(defaults: whats-new/img/ and .; hangarscope's src is relative to docs/whats-new). With a base
(--base-file, or --base-ref read with git) and --pr, entries added since the base must carry
this PR's number.

Exit code 0 = valid, 1 = errors (one per line on stderr). Standard library only.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bump import feature_prs  # noqa: E402  (same directory)

ENTRY_KEYS = {"pr", "date", "title", "description", "images"}
IMAGE_DIR = "whats-new/img/"
HTML_RE = re.compile(r"<\s*/?\s*[a-zA-Z][^>]*>")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")


def _nonempty_str(v) -> bool:
    return isinstance(v, str) and v.strip() != ""


def validate(text: str, repo_root: str | None = None, base_text: str | None = None,
             pr: int | None = None, image_prefix: str = IMAGE_DIR) -> list[str]:
    try:
        data = json.loads(text)
    except ValueError as e:
        return [f"not valid JSON: {e}"]
    if not isinstance(data, dict) or not isinstance(data.get("features"), list):
        return ['top level must be an object with a "features" array']

    errors: list[str] = []
    seen: dict[int, int] = {}
    for i, e in enumerate(data["features"]):
        at = f"features[{i}]"
        if not isinstance(e, dict):
            errors.append(f"{at}: must be an object")
            continue
        for k in sorted(set(e) - ENTRY_KEYS):
            errors.append(f"{at}: unknown field {k!r}")
        n = e.get("pr")
        if type(n) is not int or n <= 0:
            errors.append(f"{at}: \"pr\" must be a positive integer (the PR number)")
        elif n in seen:
            errors.append(f"{at}: \"pr\" {n} is already used by features[{seen[n]}]")
        else:
            seen[n] = i
        d = e.get("date")
        if not isinstance(d, str) or not DATE_RE.match(d):
            errors.append(f"{at}: \"date\" must be YYYY-MM-DD")
        else:
            try:
                datetime.date.fromisoformat(d)
            except ValueError:
                errors.append(f"{at}: \"date\" {d!r} is not a real date")
        if not _nonempty_str(e.get("title")):
            errors.append(f"{at}: \"title\" must be a non-empty string")
        desc = e.get("description")
        if not _nonempty_str(desc):
            errors.append(f"{at}: \"description\" must be a non-empty string")
        elif HTML_RE.search(desc):
            errors.append(f"{at}: \"description\" must be plain text (found HTML)")
        if "images" in e:
            errors += _check_images(at, e["images"], repo_root, image_prefix)

    # A broken base file can't tell old entries from new ones: skip that check (the PR that
    # repairs it must be able to pass).
    if pr is not None and base_text is not None and feature_prs(base_text) is not None:
        for n in sorted(set(seen) - feature_prs(base_text)):
            if n != pr:
                errors.append(f"entry with \"pr\": {n} was added by PR #{pr}; use \"pr\": {pr}")
    return errors


def _check_images(at: str, images, repo_root: str | None, image_prefix: str = IMAGE_DIR) -> list[str]:
    if not isinstance(images, list) or not 1 <= len(images) <= 2:
        return [f"{at}: \"images\" must be a list of 1-2 images (or omitted)"]
    errors = []
    for j, img in enumerate(images):
        where = f"{at}.images[{j}]"
        if not isinstance(img, dict) or set(img) != {"src", "caption"}:
            errors.append(f"{where}: must be exactly {{\"src\", \"caption\"}}")
            continue
        src, cap = img["src"], img["caption"]
        if not _nonempty_str(cap):
            errors.append(f"{where}: \"caption\" must be a non-empty string")
        if not _nonempty_str(src) or not src.startswith(image_prefix) or ".." in src:
            errors.append(f"{where}: \"src\" must be a path under {image_prefix}")
        elif repo_root is not None and not os.path.isfile(os.path.join(repo_root, src)):
            errors.append(f"{where}: {src} does not exist")
    return errors


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("path", nargs="?", default="whats-new/features.json")
    ap.add_argument("--repo-root", default=".", help="where image paths are resolved (default: .)")
    ap.add_argument("--image-prefix", default=IMAGE_DIR, help=f"every image src starts with this (default: {IMAGE_DIR})")
    ap.add_argument("--base-file", help="features.json from the PR's base, to find added entries")
    ap.add_argument("--base-ref", help="read the base's features.json at this git ref instead (missing = empty)")
    ap.add_argument("--pr", type=int, help="this PR's number; added entries must use it")
    a = ap.parse_args(argv)

    try:
        with open(a.path, encoding="utf-8") as f:
            text = f.read()
    except FileNotFoundError:
        print(f"{a.path}: not found", file=sys.stderr)
        return 1
    base = None
    if a.base_ref:
        r = subprocess.run(["git", "show", f"{a.base_ref}:{a.path}"], capture_output=True, text=True)
        base = r.stdout if r.returncode == 0 else ""
    elif a.base_file:
        try:
            with open(a.base_file, encoding="utf-8") as f:
                base = f.read()
        except FileNotFoundError:
            base = ""
    errors = validate(text, a.repo_root, base, a.pr, a.image_prefix)
    for e in errors:
        print(f"{a.path}: {e}", file=sys.stderr)
        print(f"::error file={a.path}::{e}")  # an annotation in GitHub Actions
    if not errors:
        print(f"{a.path}: OK")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
