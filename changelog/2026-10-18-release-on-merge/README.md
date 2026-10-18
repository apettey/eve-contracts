# Release on merge

**Date:** 2026-10-18

## Description

Internal: every merge to `main` now releases itself. A tested script picks the version, and the Release workflow
builds and packs it, then tags and publishes the GitHub Release (the auto-update source) only after the artefact
exists. Before, someone had to run `scripts/release.ps1` after each merge, and it tagged before building.

## Change

- `scripts/versioning/bump.py`: SemVer rules (biggest `semver:*` label wins, else minor for a new
  `whats-new/features.json` entry, else patch; never automatic major; first release 1.0.0). Modes: `next` (pure
  inputs), `pr` (preview), `release` (everything since the newest *published* tag, so a tag left by a failed run is
  stepped over rather than losing its commits; PR labels via `gh api`; Release notes).
- `scripts/versioning/validate_features.py`: valid JSON, required fields, unique `pr`, real dates, plain-text
  description, images under `whats-new/img/` that exist, entries added by a PR use its number.
- Tests: `scripts/versioning/test_bump.py` and `test_validate_features.py` (stdlib `unittest`; range mode runs
  against throwaway git repos with a fake PR lookup).
- `.github/workflows/release.yml`: now `push: main` + manual dispatch (with a forced-bump option), one run at a time:
  plan → build (the reusable workflow from the same commit) → notes. Re-runs on a tagged commit reuse the tag; a
  re-run of an already-shipped version only checks the zip and re-applies the notes. It no
  longer builds every branch push or `v*` tag push. PR CI (`ci.yml`) covers PRs.
- `.github/workflows/build-windows-app.yml`: new optional inputs `version` and `release-tag`. When set, the tag is
  created at the built commit after packing, and a re-run doesn't re-upload Velopack packages that are already
  published. Callers without them (hangarscope, multi-box) behave as before. Tag-ref builds also pass
  `--verify-tag` when they create the zip Release.
- `.github/workflows/pr-checks.yml` (new, Linux): versioning tests, features validation, and the job summary
  "Merging this PR would release vX.Y.Z". It re-runs on label changes.
- `src/EveContracts.App/EveContracts.App.csproj`: `<Version>` is now `0.0.0-dev` for local builds. There are no more
  bump commits; the tag is the version source.
- Removed `scripts/release.ps1`: its bump commit on `main` would now trigger a release of its own.
- `ci.yml`: header comment only.
- Docs: ship-change skill (`SKILL.md` Release/Version rows and step 9, `versioning-and-release.md`, checklist,
  formats), `AGENTS.md`, `README.md`, PR template.
- No app code, settings or external calls changed. Written on macOS: the 62 versioning tests pass locally,
  `actionlint` is clean, and `bump.py release` was dry-run against the real repo and API. The Windows build and the
  release workflow itself are proven by CI on this PR and by the release run when it merges.
