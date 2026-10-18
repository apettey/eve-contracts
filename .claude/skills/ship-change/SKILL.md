---
name: ship-change
description: End-to-end checklist for shipping any change to this repo — branch, tests, before/after screenshots, release-log entry, fresh-reviewer loop until a round has no findings, PR with announcement entry, a SemVer release on every merge (never skipped) and fast deploy. Use when implementing, testing, screenshotting, reviewing, opening a PR for, merging or releasing a feature, fix, refactor, CI, dependency or docs change.
---

# Ship a change

The rules file (`AGENTS.md`) wins over this skill if they disagree; update both in the same PR.

## The six non-negotiables (blocking: a change missing any of them is not done)

1. **Tests are in place.** Every code change adds/updates tests for the behaviour it changes; the full suite passes locally (Windows) and in CI. Non-code changes need CI green.
2. **Review loop.** A fresh reviewer (subagent that did not write it) reviews the diff; fix every finding; re-review; repeat until a round has **zero findings**. Round count goes in the PR body.
3. **Screenshots.** Every visible change has a `before.png` (taken *before the first edit*) and `after.png` (final version) next to its release-log entry and in the PR.
4. **Release log.** Every change, of any kind, gets a release-log entry in the same PR; visible changes also get an announcement entry.
5. **Every merged PR is a new version.** No skip. Docs/CI/deps changes are a patch.
6. **Release quickly.** Small PRs, merged the same day, released immediately after merge.

## Repo values

Porting to another repo: change this table, the review rules below it, the reviewer brief in
`references/review-loop.md`, the commands in `references/checklist.md` and `.github/pull_request_template.md`, and the
release section of `references/versioning-and-release.md`, plus `.github/workflows/release.yml`, `pr-checks.yml` and the
paths at the top of `scripts/versioning/bump.py`. Then grep the skill for `EveContracts`, `eve-contracts`, `ESI`
and `dotnet` to catch anything left over.

| Key | Value |
| --- | --- |
| Rules file | `AGENTS.md` (`CLAUDE.md` imports it) |
| Main branch | `main`. PRs are **squash-merged** (one commit per PR); not protected |
| Branch prefix | `feature/`, `fix/`, `chore/` (CI, docs, deps, process) |
| Host needed | **Windows** with .NET 10 SDK + WebView2 (WPF app, `net10.0-windows`). macOS/Linux can edit code and docs but cannot build, test, run or screenshot. |
| Dev start | `$env:EVECONTRACTS_DATA_DIR="$env:TEMP\ec-dev"; $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9222"; dotnet run --project src/EveContracts.App` |
| Dev data | No seed script: the first run downloads the SDE and scans public contracts with no login (realistic data in ~1 min). For an existing dataset, copy `%LOCALAPPDATA%\EveContracts` into `EVECONTRACTS_DATA_DIR` and delete the `Characters` rows before screenshotting. Own Contracts needs a real SSO character; stub or skip it and say so. |
| Screenshot | Connect over CDP (`http://localhost:9222`, e.g. Playwright `chromium.connectOverCDP`), `page.setViewportSize({ width: 1600, height: 1000 })` for both before and after, and screenshot the affected element/area. |
| Test dir / cmd | `tests/EveContracts.Tests/` (xUnit) · `dotnet test tests/EveContracts.Tests/EveContracts.Tests.csproj -c Release --nologo` |
| Build | `dotnet build src/EveContracts.App/EveContracts.App.csproj -c Release --nologo` **and** `dotnet build tests/EveContracts.Bench/EveContracts.Bench.csproj -c Release --nologo` (Bench uses Core and is built nowhere else) |
| Artefact build | `dotnet publish src/EveContracts.App/EveContracts.App.csproj -c Release -r win-x64 --self-contained true -o publish` (PR CI does not publish or Velopack-pack) |
| Release log | `changelog/` (index `changelog/README.md`) |
| Announcement | `whats-new/features.json` · images `whats-new/img/pr-<N>-<name>.png`. The app has no in-app What's New yet, so these become the GitHub Release notes. |
| Version source | Tags `vX.Y.Z` only, computed by `scripts/versioning/bump.py` and built with `-p:Version`. The csproj `<Version>` stays `0.0.0-dev` and is never bumped |
| Release | **Automatic on merge:** every push to `main` runs `.github/workflows/release.yml` (one run at a time): compute version → test, publish, Velopack-pack → tag + GitHub Release only after the artefact is built. PR "PR checks" summary shows `Merging this PR would release vX.Y.Z`. Manual run: `gh workflow run release.yml --ref main -f bump=<auto\|patch\|minor\|major>` |
| Deploy | Installed copies auto-update from GitHub Releases (Velopack), so publishing the Release **is** the deploy. A release that doesn't deploy isn't possible today. |

**Repo-specific review rules** (already in the reviewer brief):
- ESI etiquette: contract item lists are immutable and fetched **once per contract, ever**; list endpoints keep ETags; no polling faster than ESI's cache window; a new ESI/Fuzzwork call must be stated in the release log.
- Player-supplied text (contract titles, item names from search, character names) is rendered escaped: no `MarkupString`/raw HTML from it.
- Pure logic (evaluation, scam rules, search, formatting, parsing) lives in `EveContracts.Core` with tests; Razor components stay thin.
- The filter/slider path answers from the in-memory snapshot, never a DB query. Bulk writes go through the prepared-SQLite path, not EF change tracking (see `OPTIMIZATIONS.md`).
- Refresh tokens stay DPAPI-encrypted in the DB; nothing secret in logs, files or commits.
- `README.md` describes every user-visible feature with a `docs/img/` screenshot; update it when the UI it shows changes.

## Lifecycle

**Kinds of change:**
- **Visible:** users can see it (UI, behaviour, settings).
- **Code:** anything under `src/` or `tests/`, or a dependency bump.
- **Non-code:** docs, process, CI workflows.

Steps marked *(visible)* are for visible changes only. Steps 4, 6, 7, 8 and 9 are **never** skipped.

1. **Branch.** `git fetch origin && git switch --no-track -c <prefix><short-name> origin/main`. One change per PR, small enough to merge the same day. Another PR open? Stack on its branch, or leave `changelog/README.md` and `whats-new/features.json` alone until step 7 (see `references/formats.md`).
2. **Before screenshot** *(visible)*. Before any edit: start the dev app (Repo values), open the affected screen and capture it at the fixed viewport. Keep it in the scratchpad until the release-log folder exists. Not on Windows? **Stop and ask** the user to capture it, or move the work to a Windows host. Never substitute a mockup or the design handoff.
3. **Build with tests.** Put testable logic in Core. Write or update tests, covering empty, missing or unknown data, many characters, and ESI errors (304, 400, 403/404, 504, empty 200). A bug fix starts with a test that fails without the fix. *(code)* Run the full test cmd, both builds and the artefact build; all must pass. *(non-code)* No new tests are needed, but CI must be green on the PR. Don't commit `bin/`, `obj/` or `publish/`.
4. **Try it for real.**
   - *Code:* run the dev app through the main path, the edge cases and any setting that toggles it. Stub what you can't reach (SSO) and say so in the release log. Off Windows, this blocks: say so, don't claim it.
   - *Non-code:* check the actual output: rendered Markdown, valid JSON, a dry run or the workflow run on the PR.
5. **After screenshot** *(visible)*. Same screen, same viewport, the **final** UI; retake it if review changes the UI. Update `README.md` and its `docs/img/` screenshot if the feature is documented there.
6. **Release-log entry.** Create `changelog/YYYY-MM-DD-<short-name>/README.md` from `references/formats.md`, with `before.png`/`after.png` beside it. Record what was and wasn't run (tests, dev app, stubs). Changing an old feature gets a **new** entry; never rewrite old ones. Commit.
7. **Draft PR, announcement, index.**
   - `git fetch origin && git rebase origin/main`.
   - Add the index row at the top of `changelog/README.md`, then `git push -u origin HEAD`.
   - `gh pr create --draft` with the body from `.github/pull_request_template.md`; this gives you `<N>`.
   - *(visible)* Add the entry at the top of `whats-new/features.json` with `"pr": <N>`, and copy or crop `after.png` to `whats-new/img/pr-<N>-<name>.png`.
   - Visible **fix**, not a feature: add the `semver:patch` label, because an announcement entry defaults to minor.
   - Commit, push, and pin the PR-body image URLs to that commit's SHA (`https://raw.githubusercontent.com/apettey/eve-contracts/<sha>/changelog/...`).
8. **Review loop.** Follow `references/review-loop.md`. Spawn a fresh reviewer subagent with the diff, the intent and the checklist. Fix every finding, then re-review the *new* diff with the same reviewer (`SendMessage`). Repeat until a round has zero findings. Every later commit (rebase conflicts, retaken screenshots) gets reviewed too. Write `Review rounds: <n>, last one clean` into the PR body.
9. **Merge, version, release.**
   - `gh pr ready <N>`; CI must be green.
   - Check the "PR checks" summary: `Merging this PR would release vX.Y.Z`. The bump is the label if there is one, otherwise minor if the PR added an announcement entry, otherwise patch. Wrong? Add a `semver:*` label.
   - Merge only when the user approves: `gh pr merge <N> --squash`. Merging **is** releasing: the Release workflow on `main` builds and publishes the version (`references/versioning-and-release.md`).
   - Watch that run; check that the tag, the Release and the installer exist, and report the version.

## References

- `references/formats.md`: release-log template and index row, announcement JSON, PR body.
- `references/review-loop.md`: reviewer brief ready to paste into a subagent.
- `references/versioning-and-release.md`: bump rules, how the automatic release runs, failed releases, what to automate next.
- `references/checklist.md`: copy-paste checklist for the PR or your own tracking.
