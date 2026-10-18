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


def feature_prs(text: str | None) -> set[int] | None:
    """PR numbers in a features.json text. A missing file is empty; an unparseable one is
    None (unknown), so a PR that repairs a broken file doesn't count every old entry as new.
    validate_features.py is what fails a PR on bad JSON, not the bump."""
    if not text:
        return set()
    try:
        data = json.loads(text)
    except ValueError:
        return None
    entries = data.get("features") if isinstance(data, dict) else None
    if not isinstance(entries, list):
        return None
    return {e["pr"] for e in entries if isinstance(e, dict) and type(e.get("pr")) is int}


def added_feature_prs(before: str | None, after: str | None) -> set[int] | None:
    """Entries added between two versions of features.json; None if either side is broken."""
    b, a = feature_prs(before), feature_prs(after)
    return None if a is None or b is None else a - b


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
        added = added_feature_prs(features_before, features_after)
        if added is None:
            return Decision("patch", f"default ({FEATURES_PATH} was invalid before or after this "
                                     "change, so new entries can't be told apart; label it if minor)")
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
    added = added_feature_prs(before, after)
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


Published = Callable[[str], bool]


def plan_release(head: str, lookup: PrLookup, force_bump: str = "",
                 published: Published = lambda tag: True) -> RangePlan:
    """Plan the release for `head` (a commit on main).

    Only tags whose GitHub Release carries the Velopack packages count as released
    (`published`). A tag left behind by a run that failed after tagging is skipped over:
    its commits are released again by the next run, under a number above it.

    - head already carries a vX.Y.Z tag: reuse it (re-run of a half-released commit),
      unless a newer published release already contains head (then skip: re-building an
      older version after a newer one is out would fail to pack, or downgrade nobody).
    - head is already inside the newest published release: skip.
    - otherwise: one release covering every first-parent commit since the newest published
      tag. Normally that is exactly one squash-merged PR; it is more only when a run was
      dropped or failed.
    """
    head = git("rev-parse", head).strip()
    tags = sorted((t for t in git_tags() if parse_tag(t)), key=parse_tag, reverse=True)
    highest = parse_tag(tags[0]) if tags else None

    def newest_published(below: Version | None = None) -> str:
        for t in tags:
            if (below is None or parse_tag(t) < below) and published(t):
                return t
        return ""

    latest_pub = newest_published()
    at_head = version_tags_at(head)
    if at_head:
        tag = max(at_head, key=parse_tag)
        if latest_pub and parse_tag(latest_pub) > parse_tag(tag) and is_ancestor(head, tag_commit(latest_pub)):
            return RangePlan(True, f"{tag} at {head[:7]} was superseded by {latest_pub}, which contains it")
        previous = newest_published(parse_tag(tag))
        plan = RangePlan(False, f"{head[:7]} is already tagged {tag}; reusing it",
                         version=tag[1:], previous=previous, reused_tag=True)
        plan.commits = collect(previous, head, lookup)
        return plan

    previous = latest_pub
    if previous:
        prev_sha = tag_commit(previous)
        if is_ancestor(head, prev_sha):
            return RangePlan(True, f"{head[:7]} is already part of {previous}")
        if not is_ancestor(prev_sha, head):
            raise RuntimeError(f"{previous} ({prev_sha[:7]}) is not an ancestor of {head[:7]}")
    latest = parse_tag(previous) if previous else None

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
    version = next_version(latest, plan.bump)
    if highest is not None and version <= highest:
        # an unpublished tag (failed run) holds this number; never reuse or go below a tag
        unpublished = [t for t in tags if latest is None or parse_tag(t) > latest]
        version = next_version(highest, "patch")
        plan.reason += f"; skips unpublished {', '.join(sorted(unpublished, key=parse_tag))}"
    elif latest is None and highest is None:
        plan.reason += f"; first release is {fmt(FIRST_VERSION)}"
    plan.version = fmt(version)
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


def gh_published(repo: str) -> Published:
    """A tag counts as released when its GitHub Release (not a draft) has a -full.nupkg."""
    def published(tag: str) -> bool:
        r = subprocess.run(["gh", "api", f"repos/{repo}/releases/tags/{tag}"],
                           capture_output=True, text=True)
        if r.returncode != 0:
            if "HTTP 404" in r.stderr:
                return False
            raise RuntimeError(f"gh api releases/tags/{tag} failed: {r.stderr.strip()}")
        rel = json.loads(r.stdout)
        return not rel.get("draft") and any(
            a.get("name", "").endswith("-full.nupkg") for a in rel.get("assets", []))
    return published


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

    plan = plan_release(a.head, gh_pr_lookup(a.repo), "" if a.bump == "auto" else a.bump,
                        gh_published(a.repo))
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
