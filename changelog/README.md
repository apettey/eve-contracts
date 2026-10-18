# Release log

One entry per change, newest first. Each entry is a folder `YYYY-MM-DD-<short-name>/` with a `README.md` and, for
visible changes, `before.png` / `after.png`. Old entries are never rewritten; a change to an existing feature gets a
new entry. Format: [`.claude/skills/ship-change/references/formats.md`](../.claude/skills/ship-change/references/formats.md).

| Date | Change | Summary |
| --- | --- | --- |
| 2026-10-18 | [Shared build refuses a stale version](2026-10-18-refuse-stale-release-version/README.md) | Internal: re-running an old release run after a newer release can no longer publish older code as the latest version |
| 2026-10-18 | [PR CI: actionlint and release-log check](2026-10-18-pr-ci-lint-changelog/README.md) | Internal: PR checks lint the workflows and fail a PR without a new release-log entry (Dependabot exempt) |
| 2026-10-18 | [Prerelease input and tag cleanup](2026-10-18-release-prerelease-input/README.md) | Internal: build-windows-app.yml can publish a prerelease (to hold a deploy) and removes its tag if publishing fails |
| 2026-10-18 | [Release on merge](2026-10-18-release-on-merge/README.md) | Internal: every merge to main is versioned, built and released automatically; PRs show the version they would release |
| 2026-10-18 | [Ship-change skill](2026-10-18-ship-change-skill/README.md) | Internal: one process for every change — tests, before/after screenshots, release log, review loop, a version on every merge |
