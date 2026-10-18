# Ship-change skill

**Date:** 2026-10-18

## Description

Internal: adds the `ship-change` skill and the files it relies on, so every change is built, reviewed, documented and
released the same way. Rule 5 says every merge to `main` is a new version; before this, releases were cut only now and then (`v1.0.0` is the only tag).

## Change

- `.claude/skills/ship-change/`: `SKILL.md` (six blocking rules, repo values, lifecycle) and `references/`
  (formats, review loop with a reviewer brief, versioning/release, checklist).
- `AGENTS.md` (rules file) and `CLAUDE.md` (imports it).
- `changelog/` release-log index (this entry is the first).
- `whats-new/features.json` (empty announcement list) and `whats-new/img/`.
- `.github/pull_request_template.md`.
- No code, tests, settings or external calls changed. This is a non-code change written on macOS, so no local
  build, test run or dev-app run was possible. The evidence is CI on the PR and a fresh-reviewer loop over the skill text.
  Commands and paths were checked against `ci.yml`, `build-windows-app.yml`, `release.ps1` and `AppPaths.cs`.
- Catch-up note: #1, #2, #5, #6 and #9–#13, plus the direct docs commit `65f2be8`, landed after `v1.0.0` without
  a release. The first release under this process covers them (minor, since #12 and #13 added features). Releasing is still manual (`scripts/release.ps1`); automating
  it on merge is listed in `references/versioning-and-release.md`.
