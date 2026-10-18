#!/usr/bin/env python3
"""Shared release versioning for apettey's .NET apps (eve-contracts, hangarscope, multi-box).

Every merge to a repo's default branch is released. This script decides the version, whether
anything needs building, and the release notes. It is the one implementation behind the
composite action `.github/actions/next-version` (see its action.yml for the inputs), so the
repos never carry a copy. Per-repo differences (announcement path, release-log folder, the
bookkeeping commit, the notes title and style) are options, not forks.

Rules (the ship-change skill's references/versioning-and-release.md in each repo):
  1. A `semver:major|minor|patch` label on the merged PR wins; with several, the biggest wins.
  2. Otherwise a merge that adds a NEW `pr` entry to the announcement file is minor.
  3. Everything else is patch. Major is never automatic, and nothing is ever skipped.
  4. `deploy:manual` on any PR in a release publishes it as a prerelease.
  5. With no published vX.Y.Z tag, the first release is 1.0.0. Several merges since the last
     published tag share one version: the biggest bump wins.
  6. A failed PR lookup fails the run: labels are never guessed.

Subcommands (all print `key=value` lines, ready for $GITHUB_OUTPUT):
  next     pure inputs: changed paths, latest tag, labels (+ announcement file before/after)
  pr       "Merging this PR would release vX.Y.Z", for PR CI
  release  plan the release of a commit on the default branch, with its notes

Standard library only, so it runs on any runner with python3 >= 3.9.
"""

from __future__ import annotations

import argparse
import json
import re
import secrets
import subprocess
import sys
from dataclasses import dataclass, field
from typing import Callable, Iterable, Optional, Tuple

FIRST_VERSION = (1, 0, 0)
ORDER = {"patch": 0, "minor": 1, "major": 2}
TAG_RE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
LABEL_RE = re.compile(r"^semver:(major|minor|patch)$")
DEPLOY_MANUAL = "deploy:manual"
# The release job's own commit (multi-box: "Release vX.Y.Z: changelog and version"), and the
# old release.ps1 commits ("Release vX.Y.Z"). They carry no change of their own.
BOOKKEEPING_SUBJECT_RE = re.compile(r"^Release v\d+\.\d+\.\d+(:.*)?$")
# git's well-known empty tree, the "parent" of a root commit.
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"

Version = Tuple[int, int, int]


@dataclass(frozen=True)
class Config:
    """What differs between the repos. Defaults are eve-contracts' layout."""

    features_path: str = "whats-new/features.json"
    changelog_dir: str = "changelog"
    # Files the release job's bookkeeping commit may touch. Empty: any "Release vX.Y.Z" commit
    # is bookkeeping (eve-contracts' legacy release.ps1 commits).
    bookkeeping_paths: Tuple[str, ...] = ()
    app_name: str = ""
    runtime: str = "win-x64"
    notes_title: str = ""  # e.g. "{app} {tag}"; empty = no title line
    release_log_notes: str = "summary"  # summary: one linked line per entry; full: inline text

    @property
    def changelog_prefix(self) -> str:
        return self.changelog_dir.strip("/") + "/"


# ---------------------------------------------------------------- pure logic


def parse_tag(tag: str) -> Optional[Version]:
    m = TAG_RE.match(tag.strip())
    return (int(m[1]), int(m[2]), int(m[3])) if m else None


def latest_version(tags: Iterable[str]) -> Optional[Version]:
    """Highest plain vX.Y.Z tag; pre-release and other tags are ignored."""
    versions = [v for v in (parse_tag(t) for t in tags) if v]
    return max(versions) if versions else None


def fmt(v: Version) -> str:
    return "%d.%d.%d" % v


def next_version(latest: Optional[Version], bump: str) -> Version:
    if bump not in ORDER:
        raise ValueError(f"unknown bump {bump!r}")
    if latest is None:
        return FIRST_VERSION
    major, minor, patch = latest
    if bump == "major":
        return (major + 1, 0, 0)
    if bump == "minor":
        return (major, minor + 1, 0)
    return (major, minor, patch + 1)


def number_version(published: Optional[Version], highest: Optional[Version], bump: str) -> Version:
    """The version to release: `bump` applied to the newest published version, but always above
    every existing tag (the shared build refuses to tag below one). A tag above the published
    one was left by a failed run and has already used that bump's number, so step past it by a
    patch (published 1.0.0, dangling v1.1.0, minor -> 1.1.1: still a minor-level step for
    everyone on 1.0.0)."""
    v = next_version(published, bump)
    if highest is not None and v <= highest:
        v = next_version(highest, "patch")
    return v


def feature_prs(text: Optional[str]) -> Optional[set]:
    """PR numbers in an announcement file. A missing file is empty; an unparseable one is None
    (unknown), so a change that repairs a broken file doesn't count every old entry as new."""
    if not text or not text.strip():
        return set()
    try:
        data = json.loads(text)
    except ValueError:
        return None
    entries = data.get("features") if isinstance(data, dict) else None
    if not isinstance(entries, list):
        return None
    return {e["pr"] for e in entries if isinstance(e, dict) and type(e.get("pr")) is int}


def added_feature_prs(before: Optional[str], after: Optional[str]) -> Optional[set]:
    """Entries added between two versions of the file; None if either side is broken."""
    b, a = feature_prs(before), feature_prs(after)
    return None if a is None or b is None else a - b


def new_features(before: Optional[str], after: Optional[str]) -> list:
    """The added entries themselves, in the file's order."""
    added = added_feature_prs(before, after)
    if not added:
        return []
    return [e for e in json.loads(after)["features"] if isinstance(e, dict) and e.get("pr") in added]


def normalize_labels(labels: Iterable[str]) -> list:
    return [l.strip().lower() for l in labels if l and l.strip()]


def is_prerelease(labels: Iterable[str]) -> bool:
    return DEPLOY_MANUAL in normalize_labels(labels)


@dataclass
class Decision:
    bump: str
    reason: str


def decide_bump(
    changed_paths: Iterable[str],
    labels: Iterable[str],
    features_before: Optional[str] = None,
    features_after: Optional[str] = None,
    features_path: str = Config.features_path,
) -> Decision:
    label_bumps = [m[1] for m in (LABEL_RE.match(l) for l in normalize_labels(labels)) if m]
    if label_bumps:
        best = max(label_bumps, key=ORDER.__getitem__)
        return Decision(best, f"label semver:{best}")
    if features_path in {p.strip().replace("\\", "/") for p in changed_paths}:
        added = added_feature_prs(features_before, features_after)
        if added is None:
            return Decision("patch", f"default ({features_path} was invalid before or after this "
                                     "change, so new entries can't be told apart; label it if minor)")
        if added:
            prs = ", ".join(f"#{n}" for n in sorted(added))
            return Decision("minor", f"adds a {features_path} entry ({prs})")
    return Decision("patch", "default (no semver label, no new announcement entry)")


def max_bump(bumps: Iterable[str]) -> str:
    return max(bumps, key=ORDER.__getitem__, default="patch")


def is_bookkeeping(subject: str, paths: Iterable[str], cfg: Config) -> bool:
    """The release job's own commit: belongs to the release before it, never bumps again."""
    if not BOOKKEEPING_SUBJECT_RE.match(subject.strip()):
        return False
    if not cfg.bookkeeping_paths:
        return True
    paths = [p for p in paths if p]
    return bool(paths) and all(p in cfg.bookkeeping_paths for p in paths)


# ---------------------------------------------------------------- git helpers


def git(*args: str, check: bool = True) -> str:
    r = subprocess.run(["git", *args], capture_output=True, text=True)
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout if r.returncode == 0 else ""


def rev(ref: str) -> str:
    return git("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}", check=False).strip()


def version_tags(*args: str) -> list:
    """Plain vX.Y.Z tags (`git tag --list v*` plus `args`), highest first."""
    tags = [t for t in git("tag", "--list", "v*", *args).split() if parse_tag(t)]
    return sorted(tags, key=parse_tag, reverse=True)


def show_file(rev_: str, path: str) -> Optional[str]:
    r = subprocess.run(["git", "show", f"{rev_}:{path}"], capture_output=True, text=True)
    return r.stdout if r.returncode == 0 else None


def changed_paths(base: str, head: str) -> list:
    return [p for p in git("diff", "--name-only", "--no-renames", base, head).splitlines() if p]


def added_paths(base: str, head: str) -> list:
    return [p for p in git("diff", "--name-only", "--no-renames", "--diff-filter=A", base, head).splitlines() if p]


def first_parent(sha: str) -> str:
    return git("rev-parse", "--verify", "--quiet", f"{sha}^1", check=False).strip() or EMPTY_TREE


# ---------------------------------------------------------------- GitHub lookups


@dataclass
class PullRequest:
    number: int
    title: str
    labels: list = field(default_factory=list)


@dataclass
class ReleaseStatus:
    """A tag's GitHub Release. `published` = installed copies may be on it: not a draft, with the
    Velopack packages. `complete` adds the app zip, which the shared build attaches last."""

    exists: bool = False
    draft: bool = False
    prerelease: bool = False
    nupkg: bool = False
    zip: bool = False

    @property
    def published(self) -> bool:
        return self.exists and not self.draft and self.nupkg

    @property
    def complete(self) -> bool:
        return self.published and self.zip


PrLookup = Callable[[str], Optional[PullRequest]]
StatusLookup = Callable[[str], ReleaseStatus]


def pick_pr(sha: str, pulls: list) -> Optional[PullRequest]:
    """The PR a first-parent commit came from: the one merged as exactly this commit, else any
    merged PR containing it (a rebase merge). Open PRs that happen to contain it don't count."""
    merged = [p for p in pulls if isinstance(p, dict) and p.get("merged_at")]
    exact = [p for p in merged if p.get("merge_commit_sha") == sha]
    chosen = (exact or merged or [None])[0]
    if chosen is None:
        return None
    return PullRequest(int(chosen["number"]), str(chosen.get("title", "")),
                       [str(l.get("name", "")) for l in chosen.get("labels") or []])


def gh_pr_lookup(repo: str) -> PrLookup:
    def lookup(sha: str) -> Optional[PullRequest]:
        r = subprocess.run(["gh", "api", f"repos/{repo}/commits/{sha}/pulls"],
                           capture_output=True, text=True)
        if r.returncode != 0:
            # Fail closed: a missed deploy:manual or semver:* label would auto-update everyone.
            raise RuntimeError(f"could not look up the pull request for {sha} (labels decide the "
                               f"version and deploy:manual; not guessing them): {r.stderr.strip()}")
        return pick_pr(sha, json.loads(r.stdout))
    return lookup


def zip_name_matches(name: str, version: str, cfg: Config) -> bool:
    """The shared build's zip: <app-name>-<version>-<runtime>.zip."""
    if cfg.app_name:
        return name == f"{cfg.app_name}-{version}-{cfg.runtime}.zip"
    return re.search(rf"-{re.escape(version)}-.+\.zip$", name) is not None


def status_from_release(rel: dict, tag: str, cfg: Config) -> ReleaseStatus:
    names = [a.get("name", "") for a in rel.get("assets") or []]
    return ReleaseStatus(
        exists=True, draft=bool(rel.get("draft")), prerelease=bool(rel.get("prerelease")),
        nupkg=any(n.endswith("-full.nupkg") for n in names),
        zip=any(zip_name_matches(n, tag[1:], cfg) for n in names))


def gh_status(repo: str, cfg: Config) -> StatusLookup:
    cache: dict = {}

    def status(tag: str) -> ReleaseStatus:
        if tag not in cache:
            r = subprocess.run(["gh", "api", f"repos/{repo}/releases/tags/{tag}"],
                               capture_output=True, text=True)
            if r.returncode != 0:
                if "HTTP 404" not in r.stderr:
                    raise RuntimeError(f"gh api releases/tags/{tag} failed: {r.stderr.strip()}")
                cache[tag] = ReleaseStatus()  # no Release, or only a draft (drafts 404 here)
            else:
                cache[tag] = status_from_release(json.loads(r.stdout), tag, cfg)
        return cache[tag]
    return status


def all_published(_tag: str) -> ReleaseStatus:
    """Offline stand-in: every tag counts as published (local dry runs without gh)."""
    return ReleaseStatus(True, False, False, True, True)


# ---------------------------------------------------------------- planning


@dataclass
class Change:
    sha: str
    subject: str
    pr: Optional[PullRequest]
    decision: Decision

    @property
    def labels(self) -> list:
        return self.pr.labels if self.pr else []


@dataclass
class Plan:
    release: bool  # build and publish (false: nothing new, or the tag's Release is out already)
    finalize: bool  # write the notes (and bookkeeping): a new release, or a re-run of a tagged one
    reason: str
    version: str = ""
    bump: str = ""
    prerelease: bool = False
    previous: str = ""
    head: str = ""
    changes: list = field(default_factory=list)
    reused_tag: bool = False

    @property
    def tag(self) -> str:
        return f"v{self.version}" if self.version else ""


def skip(reason: str, head: str) -> Plan:
    return Plan(False, False, reason, head=head)


def collect(previous: str, head: str, cfg: Config, lookup: PrLookup) -> list:
    """One Change per first-parent commit in previous..head (all of head's history with no
    previous), oldest first, without the release job's bookkeeping commits."""
    spec = f"{previous}..{head}" if previous else head
    out = []
    for sha in git("rev-list", "--first-parent", "--reverse", spec).split():
        subject = git("log", "-1", "--format=%s", sha).strip()
        parent = first_parent(sha)
        paths = changed_paths(parent, sha)
        if is_bookkeeping(subject, paths, cfg):
            continue
        pr = lookup(sha)
        d = decide_bump(paths, pr.labels if pr else [], show_file(parent, cfg.features_path),
                        show_file(sha, cfg.features_path), cfg.features_path)
        if pr is None:
            d = Decision(d.bump, d.reason + f"; direct push {sha[:7]}, no PR")
        out.append(Change(sha, subject, pr, d))
    return out


def who(c: Change) -> str:
    return f"#{c.pr.number}" if c.pr else c.sha[:7]


def combine(changes: list, since: str) -> Decision:
    """One decision for several merges: the biggest of their own bumps, so one PR's
    semver:patch can't downgrade another PR's feature."""
    if len(changes) == 1:
        return changes[0].decision
    best = max_bump(c.decision.bump for c in changes)
    detail = "; ".join(f"{who(c)} {c.decision.bump}: {c.decision.reason}" for c in changes)
    return Decision(best, f"{len(changes)} merges since {since or 'the start'}, biggest bump wins ({detail})")


def newest_published(tags: Iterable[str], status: StatusLookup) -> str:
    for t in sorted(tags, key=parse_tag, reverse=True):
        if status(t).published:
            return t
    return ""


def plan_release(head: str, cfg: Config, lookup: PrLookup, status: StatusLookup,
                 force_bump: str = "") -> Plan:
    """Plan the release of `head`, a commit on the default branch.

    Only tags in head's history are a base, and only published ones: a tag left by a failed run
    is stepped over, its commits go out with this release, numbered above every existing tag.

    - head is already tagged (a re-run): reuse the tag. Build only if its Release isn't
      published yet; the notes (and bookkeeping) are always re-applied. Skip if a newer
      published release already contains head.
    - head is already inside a later tag (an old run re-run after newer merges): skip; that
      release, or the next run, covers it. Never publish older code as the newest version.
    - otherwise one release for every first-parent commit since the newest published tag,
      without bookkeeping commits. None left: nothing to release.
    """
    sha = rev(head)
    if not sha:
        raise RuntimeError(f"{head} is not a commit")
    every = version_tags()
    highest = parse_tag(every[0]) if every else None
    at_head = version_tags("--points-at", sha)
    later = [t for t in version_tags("--contains", sha) if t not in at_head]
    merged = [t for t in version_tags("--merged", sha) if t not in at_head]

    if at_head:
        tag = at_head[0]
        newer = newest_published([t for t in later if parse_tag(t) > parse_tag(tag)], status)
        if newer:
            return skip(f"{tag} at {sha[:7]} was superseded by {newer}, which contains it", sha)
        previous = newest_published([t for t in merged if parse_tag(t) < parse_tag(tag)], status)
        changes = collect(previous, sha, cfg, lookup)
        st = status(tag)
        bump = combine(changes, previous).bump if changes else "patch"
        state = ("its Release is published, so only the notes are re-applied" if st.published
                 else "its Release isn't published yet, so it is built and published")
        return Plan(not st.published, True, f"{sha[:7]} is already tagged {tag}: {state}",
                    version=tag[1:], bump=bump,
                    prerelease=any(is_prerelease(c.labels) for c in changes),
                    previous=previous, head=sha, changes=changes, reused_tag=True)

    if later:
        return skip(f"{sha[:7]} is already part of {later[-1]}", sha)

    previous = newest_published(merged, status)
    changes = collect(previous, sha, cfg, lookup)
    if not changes:
        return skip(f"no unreleased changes since {previous or 'the start'}", sha)
    d = Decision(force_bump, f"manual override: {force_bump}") if force_bump else combine(changes, previous)
    latest = parse_tag(previous) if previous else None
    version = number_version(latest, highest, d.bump)
    reason = d.reason
    if version != next_version(latest, d.bump):
        skipped = [t for t in every if latest is None or parse_tag(t) > latest]
        reason += f"; numbered above unpublished {', '.join(sorted(skipped, key=parse_tag))}"
    elif latest is None:
        reason += f"; the first release is {fmt(FIRST_VERSION)}"
    return Plan(True, True, reason, version=fmt(version), bump=d.bump,
                prerelease=any(is_prerelease(c.labels) for c in changes),
                previous=previous, head=sha, changes=changes)


def plan_preview(base: str, head: str, labels: list, cfg: Config, lookup: PrLookup,
                 status: StatusLookup) -> Plan:
    """What merging a PR (head, on top of base) would release: the unreleased merges already on
    base plus this PR, numbered as the release run would number them."""
    base_sha, head_sha = rev(base), rev(head)
    if not base_sha or not head_sha:
        raise RuntimeError(f"base {base!r} or head {head!r} is not a commit")
    mb = git("merge-base", base_sha, head_sha).strip()
    every = version_tags()
    highest = parse_tag(every[0]) if every else None
    previous = newest_published(version_tags("--merged", base_sha), status)
    pending = collect(previous, base_sha, cfg, lookup)
    own = decide_bump(changed_paths(mb, head_sha), labels, show_file(mb, cfg.features_path),
                      show_file(head_sha, cfg.features_path), cfg.features_path)
    if pending:
        bump = max_bump([own.bump] + [c.decision.bump for c in pending])
        detail = "; ".join(f"{who(c)} {c.decision.bump}" for c in pending)
        reason = (f"this PR {own.bump} ({own.reason}) plus {len(pending)} unreleased merge(s) "
                  f"on the base ({detail}), biggest bump wins")
    else:
        bump, reason = own.bump, own.reason
    latest = parse_tag(previous) if previous else None
    version = number_version(latest, highest, bump)
    prerelease = is_prerelease(labels) or any(is_prerelease(c.labels) for c in pending)
    return Plan(True, True, reason, version=fmt(version), bump=bump, prerelease=prerelease,
                previous=previous, head=head_sha, changes=pending)


# ---------------------------------------------------------------- notes


INDEX_ROW_RE = re.compile(
    r"^\|\s*(\d{4}-\d{2}-\d{2})\s*\|\s*\[([^\]]+)\]\(([^)/]+)/README\.md\)\s*\|\s*(.*?)\s*\|\s*$")


def index_rows(index: Optional[str]) -> dict:
    """Release-log index rows `| Date | [Name](folder/README.md) | Summary |`, by folder."""
    rows: dict = {}
    for line in (index or "").splitlines():
        m = INDEX_ROW_RE.match(line)
        if m:
            rows.setdefault(m[3], (m[2], m[4]))
    return rows


def release_log_entries(paths: Iterable[str], cfg: Config) -> list:
    """Entry folders whose README.md is in `paths`, oldest first (folders are dated)."""
    pre = cfg.changelog_prefix
    out = set()
    for p in paths:
        p = p.strip().replace("\\", "/")
        if p.startswith(pre) and p.endswith("/README.md") and p.count("/") == pre.count("/") + 1:
            out.add(p[len(pre):-len("/README.md")])
    return sorted(out)


def absolute_links(markdown: str, entry_dir: str, base_url: str, sha: str) -> str:
    """Relative link/image targets in an entry, rewritten to URLs pinned to the release commit."""
    def fix(m: re.Match) -> str:
        target = m[2]
        if re.match(r"^[a-z][a-z0-9+.-]*:|^#|^/", target, re.I):
            return m[0]
        url = f"{base_url}/blob/{sha}/{entry_dir}/{target}"
        if m[1].startswith("!"):
            url += "?raw=true"
        return f"{m[1]}({url})"
    return re.sub(r"(!?\[[^\]]*\])\(([^)\s]+)\)", fix, markdown)


def release_notes(plan: Plan, cfg: Config, repo: str,
                  read: Callable[[str, str], Optional[str]] = show_file,
                  added: Optional[list] = None) -> str:
    """Markdown for the GitHub Release (and multi-box's CHANGELOG.md section): an optional
    title, the announcement entries added since the previous release, the release-log entries
    added since then (one linked summary line each, or their full text), every PR, and the
    compare link. read(rev, path) reads a file at a commit; `added` defaults to the files added
    in previous..head."""
    base_url = f"https://github.com/{repo}"
    sha = plan.head
    lines: list = []
    if cfg.notes_title:
        lines += ["# " + cfg.notes_title.format(app=cfg.app_name, version=plan.version, tag=plan.tag), ""]

    before = read(plan.previous, cfg.features_path) if plan.previous else None
    features = new_features(before, read(sha, cfg.features_path))
    if features:
        lines += ["## What's new", ""]
        for f in features:
            lines += [f"### {str(f.get('title', '')).strip()}", "", str(f.get("description", "")).strip(), ""]

    if added is None:
        added = added_paths(plan.previous or EMPTY_TREE, sha)
    entries = release_log_entries(added, cfg)
    if entries:
        lines += ["## Changes", ""]
        rows = index_rows(read(sha, cfg.changelog_prefix + "README.md"))
        for e in entries:
            path = f"{cfg.changelog_prefix}{e}/README.md"
            if cfg.release_log_notes == "full":
                text = (read(sha, path) or "").strip()
                text = re.sub(r"^(#{1,4}) ", r"##\1 ", text, flags=re.M)  # nest under "## Changes"
                lines += [absolute_links(text, cfg.changelog_prefix + e, base_url, sha), ""]
            else:
                name, summary = rows.get(e, (e, ""))
                link = f"- [{name}]({base_url}/blob/{sha}/{path})"
                lines.append(f"{link}: {summary}" if summary else link)
        if cfg.release_log_notes != "full":
            lines.append("")

    lines += ["## Pull requests", ""]
    seen = set()
    for c in plan.changes:
        key = c.pr.number if c.pr else c.sha
        if key in seen:
            continue
        seen.add(key)
        lines.append(f"- {c.pr.title} (#{c.pr.number})" if c.pr else f"- {c.subject} ({c.sha[:7]})")
    if not plan.changes:
        lines.append("- (none)")
    lines.append("")
    if plan.previous:
        lines += [f"**Full diff:** {base_url}/compare/{plan.previous}...{plan.tag}", ""]
    return "\n".join(lines).rstrip() + "\n"


# ---------------------------------------------------------------- output


def emit(pairs: dict, out=None) -> None:
    out = sys.stdout if out is None else out
    for k, v in pairs.items():
        if "\n" in v:
            # random delimiter: a line of the value can never end the block early
            delim = f"EOF_{k.upper()}_{secrets.token_hex(8)}"
            out.write(f"{k}<<{delim}\n{v.rstrip(chr(10))}\n{delim}\n")
        else:
            out.write(f"{k}={v}\n")


def write_out(pairs: dict, github_output: Optional[str]) -> None:
    emit(pairs)
    if github_output:
        with open(github_output, "a", encoding="utf-8") as f:
            emit(pairs, f)


def append(path: Optional[str], text: str) -> None:
    if path:
        with open(path, "a", encoding="utf-8") as f:
            f.write(text)


def read_text(path: Optional[str]) -> Optional[str]:
    if not path:
        return None
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except FileNotFoundError:
        return None


def parse_labels(raw: str) -> list:
    """Labels as a JSON array (toJSON(github.event.pull_request.labels.*.name)) or comma list."""
    raw = (raw or "").strip()
    if not raw:
        return []
    if raw.startswith("["):
        return [str(l) for l in json.loads(raw)]
    return [l for l in (x.strip() for x in raw.split(",")) if l]


def split_list(raw: str) -> Tuple[str, ...]:
    return tuple(p for p in (x.strip() for x in re.split(r"[,\n]", raw or "")) if p)


def b(v: bool) -> str:
    return "true" if v else "false"


def add_config_args(ap: argparse.ArgumentParser) -> None:
    d = Config()
    ap.add_argument("--features-path", default=d.features_path, help="announcement file")
    ap.add_argument("--changelog-dir", default=d.changelog_dir, help="release-log folder")
    ap.add_argument("--bookkeeping-paths", default="",
                    help="files the release job's 'Release vX.Y.Z' commit may touch (comma/newline)")
    ap.add_argument("--app-name", default="", help="release asset prefix (<app>-<version>-<runtime>.zip)")
    ap.add_argument("--runtime", default=d.runtime)
    ap.add_argument("--notes-title", default="", help="first line of the notes; {app} {version} {tag}")
    ap.add_argument("--release-log-notes", default=d.release_log_notes, choices=["summary", "full"])


def config_from(a: argparse.Namespace) -> Config:
    return Config(features_path=a.features_path.strip("/"), changelog_dir=a.changelog_dir.strip("/"),
                  bookkeeping_paths=split_list(a.bookkeeping_paths), app_name=a.app_name,
                  runtime=a.runtime, notes_title=a.notes_title, release_log_notes=a.release_log_notes)


def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    n = sub.add_parser("next", help="pure inputs -> bump/version/reason/prerelease")
    n.add_argument("--latest-tag", default="", help="e.g. v1.2.3; empty = no release yet")
    n.add_argument("--labels", default="", help="PR labels: JSON array or comma-separated")
    n.add_argument("--paths-file", help="file with one changed path per line (default: stdin)")
    n.add_argument("--features-before", help="announcement file as it was before the change")
    n.add_argument("--features-after", help="announcement file as it is after the change")
    n.add_argument("--features-path", default=Config.features_path)

    for name, help_ in (("pr", "preview for a PR"), ("release", "plan the release of a commit")):
        p = sub.add_parser(name, help=help_)
        add_config_args(p)
        p.add_argument("--repo", default="", help="owner/name, for PR lookups, Release state and links")
        p.add_argument("--offline", action="store_true",
                       help="no GitHub: every tag counts as published and no commit has a PR (dry runs)")
        p.add_argument("--github-output", help="also append the outputs to this file ($GITHUB_OUTPUT)")
        p.add_argument("--summary", help="append a Markdown summary to this file ($GITHUB_STEP_SUMMARY)")
        if name == "pr":
            p.add_argument("--base", required=True, help="the PR's base commit")
            p.add_argument("--head", default="HEAD", help="the PR's head (or its test merge commit)")
            p.add_argument("--labels", default="", help="the PR's labels: JSON array or comma-separated")
        else:
            p.add_argument("--head", default="HEAD")
            p.add_argument("--bump", default="auto", choices=["auto", "patch", "minor", "major"])

    a = ap.parse_args(argv)

    if a.cmd == "next":
        paths = (read_text(a.paths_file) or "") if a.paths_file else sys.stdin.read()
        latest = parse_tag(a.latest_tag) if a.latest_tag else None
        if a.latest_tag and latest is None:
            ap.error(f"--latest-tag {a.latest_tag!r} is not vX.Y.Z")
        labels = parse_labels(a.labels)
        d = decide_bump(paths.splitlines(), labels, read_text(a.features_before),
                        read_text(a.features_after), a.features_path)
        emit({"bump": d.bump, "version": fmt(next_version(latest, d.bump)), "reason": d.reason,
              "prerelease": b(is_prerelease(labels))})
        return 0

    if not a.offline and not a.repo:
        ap.error("--repo is required (or --offline for a dry run)")
    cfg = config_from(a)
    lookup = (lambda sha: None) if a.offline else gh_pr_lookup(a.repo)
    status = all_published if a.offline else gh_status(a.repo, cfg)
    repo = a.repo or "OWNER/REPO"

    if a.cmd == "pr":
        plan = plan_preview(a.base, a.head, parse_labels(a.labels), cfg, lookup, status)
        hold = (" as a **prerelease** (`deploy:manual`: installed copies don't update to it until it "
                "is promoted)") if plan.prerelease else ""
        text = f"Merging this PR would release **{plan.tag}** ({plan.bump}: {plan.reason}){hold}."
        write_out({"version": plan.version, "tag": plan.tag, "bump": plan.bump, "reason": plan.reason,
                   "prerelease": b(plan.prerelease), "previous": plan.previous, "preview": text},
                  a.github_output)
        append(a.summary, f"### Merging this PR would release {plan.tag}\n\n"
                          f"**{plan.bump}** bump from {plan.previous or 'no release yet'}: {plan.reason}.\n\n"
                          + (f"Published{hold}.\n\n" if hold else "")
                          + "_If another PR merges first, the number moves up. Change it with a "
                            "`semver:major|minor|patch` label._\n")
        return 0

    plan = plan_release(a.head, cfg, lookup, status, "" if a.bump == "auto" else a.bump)
    notes = release_notes(plan, cfg, repo) if plan.finalize else ""
    write_out({"release": b(plan.release), "finalize": b(plan.finalize), "version": plan.version,
               "tag": plan.tag, "bump": plan.bump, "reason": plan.reason,
               "prerelease": b(plan.prerelease), "previous": plan.previous, "sha": plan.head,
               "notes": notes}, a.github_output)
    if not plan.finalize:
        append(a.summary, f"### No release: {plan.reason}\n")
    else:
        hold = ("\n\n**Prerelease** (`deploy:manual`): installed copies ignore it until it is promoted "
                f"with `gh release edit {plan.tag} --prerelease=false`.") if plan.prerelease else ""
        build = "" if plan.release else "\n\nAlready published: nothing is built, the notes are re-applied."
        append(a.summary, f"### Releasing {plan.tag}\n\n**{plan.bump}**: {plan.reason}.{hold}{build}\n\n"
                          f"<details><summary>Release notes</summary>\n\n{notes}\n</details>\n")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except RuntimeError as e:
        print(f"::error::{e}", file=sys.stderr)
        sys.exit(1)
