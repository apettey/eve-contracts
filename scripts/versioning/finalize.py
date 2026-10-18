#!/usr/bin/env python3
"""After the shared build has published a release: set its notes, check it is complete, and
(optionally) record it in a generated CHANGELOG.md and the app's <Version> with a bookkeeping
commit. The one implementation behind `.github/actions/finalize-release`.

  finalize.py --repo o/r --tag vX.Y.Z --notes-file notes.md [--prerelease true|false]
              [--build-result success|skipped] [--app-name A --runtime win-x64]
              [--changelog-file CHANGELOG.md] [--version-file src/App/App.csproj]
              [--branch main] [--dry-run]

Order: notes first (always), then the Release's state. A draft or a Release without the
Velopack packages has not shipped, so the run fails before any bookkeeping. A published
Release without the zip has shipped: the bookkeeping still runs, then the run fails so a
person attaches the zip. The bookkeeping commit ("Release vX.Y.Z: changelog and version")
goes on top of the latest branch tip and is retried if another merge lands meanwhile;
bump.py ignores it, so it never causes another release. Re-running is idempotent.

Standard library only.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import subprocess
import sys
import tempfile
from typing import Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bump import Config, ReleaseStatus, status_from_release  # noqa: E402

BOT = ("github-actions[bot]", "41898282+github-actions[bot]@users.noreply.github.com")


# ---------------------------------------------------------------- pure logic


def demote(notes: str) -> str:
    """Release notes as a CHANGELOG.md section body: the title line dropped, headings one level
    down so they nest under "## [X.Y.Z] - date"."""
    lines = notes.strip("\n").split("\n")
    if lines and lines[0].startswith("# "):
        lines = lines[1:]
        while lines and not lines[0].strip():
            lines = lines[1:]
    return "\n".join(re.sub(r"^(#{1,5}) ", r"#\1 ", l) for l in lines)


def update_changelog(content: str, version: str, date: str, notes: str) -> str:
    """Inserts a "## [X.Y.Z] - date" section (the notes) above the newest release. A leftover
    hand-written "## [Unreleased]" section is folded into it, so its text ships with the first
    release after it. A version already in the file returns the content unchanged."""
    nl = "\r\n" if "\r\n" in content else "\n"
    if re.search(rf"(?m)^## \[{re.escape(version)}\]", content):
        return content
    lines = re.split(r"\r?\n", content)
    unreleased: list = []
    u = next((i for i, l in enumerate(lines) if re.match(r"^## \[Unreleased\]\s*$", l)), -1)
    if u >= 0:
        end = next((i for i in range(u + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
        unreleased = lines[u + 1:end]
        del lines[u:end]
    at = next((i for i, l in enumerate(lines) if l.startswith("## ")), len(lines))

    section = [f"## [{version}] - {date}", ""] + re.split(r"\r?\n", notes.rstrip())
    while unreleased and not unreleased[0].strip():
        unreleased = unreleased[1:]
    if unreleased:
        section += ["", "### Earlier, unversioned changes", ""]
        section += [re.sub(r"^### ", "#### ", l) for l in unreleased]
    while section and not section[-1].strip():
        section.pop()
    section.append("")
    lines[at:at] = section
    return nl.join(lines)


VERSION_RE = re.compile(r"<Version>\s*([^<]*?)\s*</Version>")


def set_project_version(content: str, version: str) -> str:
    """Raises <Version> in csproj text to `version`; never lowers it."""
    m = VERSION_RE.search(content)
    if not m:
        raise ValueError("no <Version> property in the project file")
    cur = re.match(r"^(\d+)\.(\d+)\.(\d+)$", m[1])
    new = tuple(int(x) for x in version.split("."))
    if cur and tuple(int(x) for x in cur.groups()) >= new:
        return content
    return content[:m.start()] + f"<Version>{version}</Version>" + content[m.end():]


def state_messages(tag: str, st: ReleaseStatus, labels_prerelease: bool, build_result: str) -> tuple:
    """(summary, warnings, errors, ship_ok) for the Release as it really is."""
    summary = (f"Published as a **prerelease**: installed copies don't update to it. Once the outside "
               f"work is done, promote it: `gh release edit {tag} --prerelease=false`"
               if st.prerelease else "Published as a normal release. Installed copies auto-update to it.")
    warnings, errors = [], []
    # A promoted deploy:manual release (labels hold, Release stable) is the normal end state of a
    # re-run, so only a freshly built one is worth a warning. Never suggest re-holding.
    if st.prerelease and not labels_prerelease:
        warnings.append(f"{tag} is a prerelease although no PR in it has deploy:manual (the Release "
                        f"existed before this run). If nothing is waiting, promote it: "
                        f"gh release edit {tag} --prerelease=false")
    elif not st.prerelease and labels_prerelease and build_result == "success":
        warnings.append(f"{tag} has deploy:manual but was already a normal release before this run, so "
                        "installed copies may already update to it. Check the outside work is done.")
    shipped = st.published
    if not st.exists:
        errors.append(f"{tag} has no published Release. Run the release workflow again on the default branch.")
    elif not shipped:
        errors.append(f"{tag} is incomplete (draft: {st.draft}, Velopack package: {st.nupkg}). Publish "
                      f"the draft with 'gh release edit {tag} --draft=false' if it has every file, or "
                      "delete the draft and run the release workflow again on the default branch.")
    elif not st.zip:
        errors.append(f"{tag} has shipped but has no zip. Attach it by hand from the build run's "
                      "artifacts (gh release upload); the notes are set, and this check fails on every "
                      "run for this commit until the zip is there.")
    return summary, warnings, errors, shipped


# ---------------------------------------------------------------- side effects


def run(*cmd: str, check: bool = True, env: Optional[dict] = None) -> subprocess.CompletedProcess:
    r = subprocess.run(list(cmd), capture_output=True, text=True,
                       env={**os.environ, **env} if env else None)
    if check and r.returncode != 0:
        raise RuntimeError(f"{' '.join(cmd[:3])} ... failed: {(r.stderr or '').strip()}")
    return r


def release_status(repo: str, tag: str, cfg: Config) -> ReleaseStatus:
    # The list endpoint also returns drafts (releases/tags/<tag> 404s on them).
    r = run("gh", "api", "--paginate", f"repos/{repo}/releases",
            "--jq", ".[] | select(.tag_name == env.RELEASE_TAG)", env={"RELEASE_TAG": tag})
    rels = [json.loads(l) for l in r.stdout.splitlines() if l.strip()]
    if not rels:
        return ReleaseStatus()
    rels.sort(key=lambda x: bool(x.get("draft")))  # a published one wins over a stray draft
    return status_from_release(rels[0], tag, cfg)


def bookkeeping(tag: str, notes: str, branch: str, changelog_file: str, version_file: str,
                dry_run: bool) -> None:
    version = tag[1:]
    for attempt in range(1, 4):
        if not dry_run:
            run("git", "fetch", "-q", "origin", branch)
            run("git", "checkout", "-q", "-B", "release-bookkeeping", f"origin/{branch}")
        date = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")
        changed = []
        if changelog_file:
            with open(changelog_file, encoding="utf-8", newline="") as f:
                old = f.read()
            new = update_changelog(old, version, date, demote(notes))
            if new != old:
                changed.append((changelog_file, new))
        if version_file:
            with open(version_file, encoding="utf-8", newline="") as f:
                old = f.read()
            new = set_project_version(old, version)
            if new != old:
                changed.append((version_file, new))
        if not changed:
            print(f"{changelog_file or version_file} already record {tag}.")
            return
        if dry_run:
            for path, text in changed:
                print(f"----- {path} (top) -----")
                print("\n".join(text.splitlines()[:40]))
            return
        for path, text in changed:
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(text)
        paths = [p for p, _ in changed]
        run("git", "-c", f"user.name={BOT[0]}", "-c", f"user.email={BOT[1]}", "commit", "-q",
            "-m", f"Release {tag}: changelog and version", "--", *paths)
        if run("git", "push", "-q", "origin", f"HEAD:{branch}", check=False).returncode == 0:
            print(f"Recorded {tag} in {', '.join(paths)}.")
            return
        print(f"Push rejected ({branch} moved); retrying ({attempt}/3).")
    raise RuntimeError(f"could not push the bookkeeping commit for {tag}")


def main(argv: Optional[list] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo", required=True)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--notes-file", required=True)
    ap.add_argument("--prerelease", default="false", choices=["true", "false"],
                    help="whether the release's PRs carry deploy:manual (for the warnings)")
    ap.add_argument("--build-result", default="success", help="the build job's result")
    ap.add_argument("--app-name", default="")
    ap.add_argument("--runtime", default="win-x64")
    ap.add_argument("--changelog-file", default="", help="generated changelog to add the section to")
    ap.add_argument("--version-file", default="", help="csproj whose <Version> records the release")
    ap.add_argument("--branch", default="main", help="where the bookkeeping commit goes")
    ap.add_argument("--summary", help="append a Markdown summary here ($GITHUB_STEP_SUMMARY)")
    ap.add_argument("--dry-run", action="store_true",
                    help="change nothing: print the notes and the bookkeeping edits")
    a = ap.parse_args(argv)
    if not re.match(r"^v\d+\.\d+\.\d+$", a.tag):
        ap.error(f"--tag {a.tag!r} is not vX.Y.Z")
    with open(a.notes_file, encoding="utf-8") as f:
        notes = f.read()
    if not notes.strip():
        raise RuntimeError("the release notes are empty")

    if a.dry_run:
        print(f"----- release notes for {a.tag} -----\n{notes}")
        if a.changelog_file or a.version_file:
            bookkeeping(a.tag, notes, a.branch, a.changelog_file, a.version_file, True)
        return 0

    run("gh", "release", "edit", a.tag, "--repo", a.repo, "--notes-file", a.notes_file)
    st = release_status(a.repo, a.tag, Config(app_name=a.app_name, runtime=a.runtime))
    summary, warnings, errors, shipped = state_messages(a.tag, st, a.prerelease == "true", a.build_result)
    if a.summary:
        with open(a.summary, "a", encoding="utf-8") as f:
            f.write(f"## {a.tag}\n\n{summary}\n")
    for w in warnings:
        print(f"::warning::{w}")
    if shipped and (a.changelog_file or a.version_file):
        bookkeeping(a.tag, notes, a.branch, a.changelog_file, a.version_file, False)
    for e in errors:
        print(f"::error::{e}")
    return 1 if errors else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, ValueError) as e:
        print(f"::error::{e}", file=sys.stderr)
        sys.exit(1)
