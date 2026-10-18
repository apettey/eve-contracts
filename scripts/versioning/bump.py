#!/usr/bin/env python3
"""Decide the SemVer bump and version for a merged PR (or a PR about to merge).

Rules (see .claude/skills/ship-change/references/versioning-and-release.md):
  1. A `semver:major|minor|patch` label wins; with several, the biggest wins.
  2. Otherwise a PR that adds an entry to whats-new/features.json is minor.
  3. Everything else is patch. Major is never automatic.
  4. With no vX.Y.Z tag yet, the first release is 1.0.0.

Subcommands (all print `key=value` lines, ready for $GITHUB_OUTPUT):
  next     pure inputs: changed paths, latest tag, labels (+ features.json before/after)
  pr       a PR's diff between two commits, for the "would release" preview in PR CI
  release  every untagged commit on main up to --head, for the release workflow

Standard library only, so it runs on any runner with python3.
"""

from __future__ import annotations

import argparse
import json
import re
import secrets
import subprocess
import sys
from dataclasses import dataclass, field
from typing import Callable, Iterable

FEATURES_PATH = "whats-new/features.json"
CHANGELOG_DIR = "changelog/"
FIRST_VERSION = (1, 0, 0)
ORDER = {"patch": 0, "minor": 1, "major": 2}
TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
LABEL_RE = re.compile(r"^semver:(major|minor|patch)$")
# release.ps1's old bookkeeping commits; they carry no change of their own.
LEGACY_RELEASE_COMMIT_RE = re.compile(r"^Release v\d+\.\d+\.\d+$")
# git's well-known empty tree, the "parent" of a root commit.
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"

Version = tuple[int, int, int]


# ---------------------------------------------------------------- pure logic


def parse_tag(tag: str) -> Version | None:
    m = TAG_RE.match(tag.strip())
    return (int(m[1]), int(m[2]), int(m[3])) if m else None


def latest_version(tags: Iterable[str]) -> Version | None:
    """Highest plain vX.Y.Z tag; pre-release and other tags are ignored."""
    versions = [v for v in (parse_tag(t) for t in tags) if v]
    return max(versions) if versions else None


def fmt(v: Version) -> str:
    return "%d.%d.%d" % v


def next_version(latest: Version | None, bump: str) -> Version:
    if latest is None:
        return FIRST_VERSION
    major, minor, patch = latest
    if bump == "major":
        return (major + 1, 0, 0)
    if bump == "minor":
        return (major, minor + 1, 0)
    if bump == "patch":
        return (major, minor, patch + 1)
    raise ValueError(f"unknown bump {bump!r}")


def feature_prs(text: str | None) -> set[int]:
    """PR numbers in a features.json text. Missing or unparseable counts as empty:
    validate_features.py is what fails a PR on bad JSON, not the bump."""
    if not text:
        return set()
    try:
        data = json.loads(text)
    except ValueError:
        return set()
    entries = data.get("features") if isinstance(data, dict) else None
    if not isinstance(entries, list):
        return set()
    return {e["pr"] for e in entries if isinstance(e, dict) and type(e.get("pr")) is int}


@dataclass
class Decision:
    bump: str
    reason: str


def decide_bump(
    changed_paths: Iterable[str],
    labels: Iterable[str],
    features_before: str | None = None,
    features_after: str | None = None,
) -> Decision:
    label_bumps = [m[1] for m in (LABEL_RE.match(l.strip().lower()) for l in labels) if m]
    if label_bumps:
        best = max(label_bumps, key=ORDER.__getitem__)
        return Decision(best, f"label semver:{best}")
    if FEATURES_PATH in {p.strip() for p in changed_paths}:
        added = feature_prs(features_after) - feature_prs(features_before)
        if added:
            prs = ", ".join(f"#{n}" for n in sorted(added))
            return Decision("minor", f"adds a {FEATURES_PATH} entry ({prs})")
    return Decision("patch", "default (no semver label, no new announcement entry)")


def max_bump(bumps: Iterable[str]) -> str:
    return max(bumps, key=ORDER.__getitem__, default="patch")


# ---------------------------------------------------------------- git helpers


def git(*args: str, check: bool = True) -> str:
    r = subprocess.run(["git", *args], capture_output=True, text=True)
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout if r.returncode == 0 else ""


def git_tags() -> list[str]:
    return git("tag", "--list", "v*").split()


def show_file(rev: str, path: str) -> str | None:
    r = subprocess.run(["git", "show", f"{rev}:{path}"], capture_output=True, text=True)
    return r.stdout if r.returncode == 0 else None


def changed_paths(base: str, head: str) -> list[str]:
    return [p for p in git("diff", "--name-only", "--no-renames", base, head).splitlines() if p]


def tag_commit(tag: str) -> str:
    return git("rev-list", "-n", "1", tag).strip()


def is_ancestor(a: str, b: str) -> bool:
    return subprocess.run(["git", "merge-base", "--is-ancestor", a, b]).returncode == 0


def version_tags_at(rev: str) -> list[str]:
    return [t for t in git("tag", "--points-at", rev).split() if parse_tag(t)]


# ---------------------------------------------------------------- range (release) mode


@dataclass
class PullRequest:
    number: int
    title: str
    labels: list[str] = field(default_factory=list)


PrLookup = Callable[[str], "PullRequest | None"]


@dataclass
class CommitRelease:
    sha: str
    subject: str
    pr: PullRequest | None
    decision: Decision
    added_features: list[dict]
    changelog_dirs: list[str]


@dataclass
class RangePlan:
    skip: bool
    reason: str
    version: str = ""
    bump: str = ""
    commits: list[CommitRelease] = field(default_factory=list)
    previous: str = ""
    reused_tag: bool = False


def new_features(before: str | None, after: str | None) -> list[dict]:
    added = feature_prs(after) - feature_prs(before)
    if not added:
        return []
    entries = json.loads(after)["features"]
    return [e for e in entries if isinstance(e, dict) and e.get("pr") in added]


def changelog_dirs(paths: Iterable[str]) -> list[str]:
    dirs = []
    for p in paths:
        if p.startswith(CHANGELOG_DIR) and p.endswith("/README.md") and p.count("/") == 2:
            dirs.append(p.rsplit("/", 1)[0])
    return sorted(set(dirs))


def collect(previous: str, head: str, lookup: PrLookup) -> list[CommitRelease]:
    """One entry per first-parent commit in previous..head (all of head's history if no previous)."""
    spec = f"{previous}..{head}" if previous else head
    out = []
    for sha in git("rev-list", "--first-parent", "--reverse", spec).split():
        subject = git("log", "-1", "--format=%s", sha).strip()
        if LEGACY_RELEASE_COMMIT_RE.match(subject):
            continue
        parent = git("rev-parse", "--verify", "--quiet", f"{sha}^", check=False).strip() or EMPTY_TREE
        paths = changed_paths(parent, sha)
        before, after = show_file(parent, FEATURES_PATH), show_file(sha, FEATURES_PATH)
        pr = lookup(sha)
        d = decide_bump(paths, pr.labels if pr else [], before, after)
        if pr is None:
            d = Decision(d.bump, d.reason + f"; direct push {sha[:7]}, no PR")
        out.append(CommitRelease(sha, subject, pr, d, new_features(before, after), changelog_dirs(paths)))
    return out


def plan_release(head: str, lookup: PrLookup, force_bump: str = "") -> RangePlan:
    """Plan the release for `head` (a commit on main).

    - head already carries a vX.Y.Z tag: reuse it (re-run of a released or half-released commit).
    - head is already inside the newest tag's history: skip (a newer run released it).
    - otherwise: one release covering every first-parent commit since the newest tag. Normally
      that is exactly one squash-merged PR; it is more only when a run was cancelled or failed.
    """
    head = git("rev-parse", head).strip()
    tags = git_tags()

    at_head = version_tags_at(head)
    if at_head:
        tag = max(at_head, key=parse_tag)
        below = [t for t in tags if parse_tag(t) and parse_tag(t) < parse_tag(tag)]
        lower = latest_version(below)
        previous = "v" + fmt(lower) if lower else ""
        plan = RangePlan(False, f"{head[:7]} is already tagged {tag}; reusing it",
                         version=tag[1:], previous=previous, reused_tag=True)
        plan.commits = collect(previous, head, lookup)
        return plan

    latest = latest_version(tags)
    previous = ""
    if latest is not None:
        previous = "v" + fmt(latest)
        prev_sha = tag_commit(previous)
        if is_ancestor(head, prev_sha):
            return RangePlan(True, f"{head[:7]} is already part of {previous}")
        if not is_ancestor(prev_sha, head):
            raise RuntimeError(f"{previous} ({prev_sha[:7]}) is not an ancestor of {head[:7]}")

    plan = RangePlan(False, "", previous=previous, commits=collect(previous, head, lookup))
    if not plan.commits:
        return RangePlan(True, f"no releasable commits since {previous or 'the start'}")

    if force_bump:
        plan.bump = force_bump
        plan.reason = f"manual override: {force_bump}"
    elif len(plan.commits) == 1:
        plan.bump = plan.commits[0].decision.bump
        plan.reason = plan.commits[0].decision.reason
    else:
        plan.bump = max_bump(c.decision.bump for c in plan.commits)
        plan.reason = (f"{len(plan.commits)} commits since {previous or 'the start'}, "
                       f"biggest bump wins: {plan.bump}")
    plan.version = fmt(next_version(latest, plan.bump))
    if latest is None:
        plan.reason += f"; first release is {fmt(FIRST_VERSION)}"
    return plan


def release_notes(plan: RangePlan, repo: str) -> str:
    base = f"https://github.com/{repo}"
    lines: list[str] = []
    for c in plan.commits:
        for f in c.added_features:
            lines += [f"## {f.get('title', '')}", "", str(f.get("description", "")), ""]
    lines.append("## Changes")
    lines.append("")
    for c in plan.commits:
        ref = f"#{c.pr.number}" if c.pr else f"{c.sha[:7]}"
        title = c.pr.title if c.pr else c.subject
        entry = f"- {title} ({ref})"
        for d in c.changelog_dirs:
            entry += f" · [release log]({base}/blob/{c.sha}/{d}/README.md)"
        lines.append(entry)
    lines.append("")
    if plan.previous:
        lines.append(f"Full diff: {base}/compare/{plan.previous}...v{plan.version}")
    if not plan.reused_tag:
        lines.append(f"Bump: **{plan.bump}** ({plan.reason})")
    return "\n".join(lines) + "\n"


def gh_pr_lookup(repo: str) -> PrLookup:
    def lookup(sha: str) -> PullRequest | None:
        r = subprocess.run(["gh", "api", f"repos/{repo}/commits/{sha}/pulls"],
                           capture_output=True, text=True)
        if r.returncode != 0:
            raise RuntimeError(f"gh api commits/{sha}/pulls failed: {r.stderr.strip()}")
        prs = [p for p in json.loads(r.stdout) if p.get("merged_at")]
        if not prs:
            return None
        p = prs[0]
        return PullRequest(p["number"], p["title"], [l["name"] for l in p.get("labels", [])])
    return lookup


# ---------------------------------------------------------------- output


def emit(pairs: dict[str, str], out=None) -> None:
    out = sys.stdout if out is None else out
    for k, v in pairs.items():
        if "\n" in v:
            # random delimiter: a line of the value can never end the block early
            delim = f"EOF_{k.upper()}_{secrets.token_hex(8)}"
            out.write(f"{k}<<{delim}\n{v.rstrip(chr(10))}\n{delim}\n")
        else:
            out.write(f"{k}={v}\n")


def read_text(path: str | None) -> str | None:
    if not path:
        return None
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except FileNotFoundError:
        return None


def split_labels(s: str) -> list[str]:
    return [l for l in (x.strip() for x in s.split(",")) if l]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    n = sub.add_parser("next", help="pure inputs -> bump/version/reason")
    n.add_argument("--latest-tag", default="", help="e.g. v1.2.3; empty = no release yet")
    n.add_argument("--labels", default="", help="comma-separated PR labels")
    n.add_argument("--paths-file", help="file with one changed path per line (default: stdin)")
    n.add_argument("--features-before", help="features.json as it was before the PR")
    n.add_argument("--features-after", help="features.json as it is after the PR")

    p = sub.add_parser("pr", help="preview for a PR: diff base..head in this checkout")
    p.add_argument("--base", required=True)
    p.add_argument("--head", required=True)
    p.add_argument("--labels", default="")

    r = sub.add_parser("release", help="plan the release for a commit on main")
    r.add_argument("--head", default="HEAD")
    r.add_argument("--repo", required=True, help="owner/name, for PR lookup and links")
    r.add_argument("--bump", default="auto", choices=["auto", "patch", "minor", "major"])

    a = ap.parse_args(argv)

    if a.cmd == "next":
        paths = (read_text(a.paths_file) or "") if a.paths_file else sys.stdin.read()
        latest = parse_tag(a.latest_tag) if a.latest_tag else None
        if a.latest_tag and latest is None:
            ap.error(f"--latest-tag {a.latest_tag!r} is not vX.Y.Z")
        d = decide_bump(paths.splitlines(), split_labels(a.labels),
                        read_text(a.features_before), read_text(a.features_after))
        emit({"bump": d.bump, "version": fmt(next_version(latest, d.bump)), "reason": d.reason})
        return 0

    if a.cmd == "pr":
        latest = latest_version(git_tags())
        d = decide_bump(changed_paths(a.base, a.head), split_labels(a.labels),
                        show_file(a.base, FEATURES_PATH), show_file(a.head, FEATURES_PATH))
        emit({"bump": d.bump, "version": fmt(next_version(latest, d.bump)), "reason": d.reason,
              "latest": "v" + fmt(latest) if latest else ""})
        return 0

    plan = plan_release(a.head, gh_pr_lookup(a.repo), "" if a.bump == "auto" else a.bump)
    out = {"skip": "true" if plan.skip else "false", "reason": plan.reason,
           "bump": plan.bump, "version": plan.version,
           "tag": f"v{plan.version}" if plan.version else "",
           "reused": "true" if plan.reused_tag else "false"}
    if not plan.skip:
        out["notes"] = release_notes(plan, a.repo)
    emit(out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
