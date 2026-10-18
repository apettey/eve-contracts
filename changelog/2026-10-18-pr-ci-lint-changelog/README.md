# PR CI: actionlint and release-log check

**Date:** 2026-10-18

## Description

Internal: the "PR checks" workflow now lints every workflow with actionlint and fails a PR that adds no new
`changelog/YYYY-MM-DD-<name>/README.md`. Both were listed under "Automate next" in the ship-change skill. Dependabot
PRs are exempt from the release-log check because they can't write an entry.

## Change

- `scripts/versioning/check_changelog.py` (new): passes when the PR (`HEAD^1..HEAD` of the merge checkout) adds a
  `changelog/<folder>/README.md` whose folder didn't exist at the base, with a real date and a lower-case hyphenated
  name. Editing an old entry doesn't count. `--author dependabot[bot]` is exempt; other bots and humans are not.
- `scripts/versioning/test_check_changelog.py` (new): unit tests for the rules plus CLI tests against throwaway git
  repos with a PR-style merge commit. They run in the existing `unittest discover` step.
- `.github/workflows/pr-checks.yml`: two new steps in `release-checks`. actionlint is pinned to 1.7.12 and the
  download is checked against its SHA-256. The release-log step gets the PR author through `env`, not
  interpolated into `run:`.
- Docs: `AGENTS.md`, ship-change `SKILL.md`, `versioning-and-release.md` (removed from "Automate next"),
  `checklist.md` and `formats.md` describe the new checks and the Dependabot exemption.
- No app code, settings or external calls changed. Written on macOS: the versioning tests pass locally,
  `actionlint` is clean, and `check_changelog.py` was run against this branch. The workflow steps themselves are
  proven by the PR checks run on this PR.
