# Shared release versioning for all three apps

**Date:** 2026-10-18

## Description

eve-contracts, hangarscope and multi-box each had their own copy of the release-versioning code (Python here,
Python in hangarscope, PowerShell in multi-box) for the same rules. There is now one tested implementation here,
exposed as composite actions that the other two call instead of keeping a copy. This repo's release and PR checks
use the same actions from the same commit.

## Change

- `scripts/versioning/bump.py` is now the shared engine, with the per-repo differences as options (announcement
  path, release-log folder, bookkeeping files, app name, notes title and style). New in it, from the other repos:
  only tags in the commit's history count as a base; a commit already inside a later tag is skipped; the release
  job's `Release vX.Y.Z: ...` bookkeeping commit is ignored; `deploy:manual` sets `prerelease`; a tagged commit
  whose Release is published only re-applies the notes (`release=false`, `finalize=true`); the PR preview includes
  unreleased merges already on `main`. Versions are still numbered above every existing tag.
- Notes options for the other repos: `notes-title` (hangarscope's `# HangarScope vX.Y.Z`), `release-log-notes:
  full` (hangarscope's inline entry text) and `announcement-notes: list` (multi-box's `- **Title.** text (#N)`).
  hangarscope's "No release-log entry in this release. Commits:" fallback is replaced by the `## Pull requests`
  list, which every release has.
- A `Release vX.Y.Z...` commit counts as bookkeeping only if no merged PR produced it, so a squash-merged PR with
  such a title keeps its labels.
- The announcement validator no longer crashes when the base file is invalid JSON (the repairing PR can pass).
- New `scripts/versioning/finalize.py`: sets the Release notes, fails on a draft or a Release without the Velopack
  packages or zip, warns on a prerelease state that doesn't match the labels, and (multi-box only) writes the
  generated `CHANGELOG.md` section and csproj `<Version>` in a bookkeeping commit.
- `scripts/versioning/validate_features.py`: image prefix and root are options (hangarscope's image paths are
  relative to `docs/whats-new`), `--base-ref` reads the base file with git, errors are also Actions annotations.
- New composite actions `.github/actions/next-version`, `finalize-release` and `validate-features`. Callers use
  `apettey/eve-contracts/.github/actions/<name>@main`; the runner downloads this repo at that ref, so the scripts
  always come from the same commit as the action.
- `release.yml`: jobs `version` / `build` / `finalize` on the shared actions. **Behaviour change:** `deploy:manual`
  now publishes a prerelease here too (it used to mean "do the manual step before merging"), matching the other two.
  Release notes change shape: `## What's new` (new announcement entries), `## Changes` (release-log entries added
  since the previous release, each linked with its index summary), `## Pull requests` (every PR or direct push) and
  the compare link. The old `Bump: ...` line is gone from the notes (it is in the run summary instead).
- `pr-checks.yml`: the features check and version preview use the shared actions (`pull-requests: read` added for
  the preview's lookups of unreleased merges).
- Tests: the three former suites merged into `test_bump.py` (plus `test_finalize.py` and new validator cases),
  147 tests in all. Dry runs against this repo's real history give the same decisions as the old script at the last
  six commits on `main`, and `v1.1.4` for #19 with the `v1.1.4` tag removed in a scratch clone.
- Not run: a release on GitHub with the new workflow (it runs on the merge).
