# Versioning and release

## Every merged PR is a release

SemVer `MAJOR.MINOR.PATCH`, tag `vX.Y.Z`, decided per merged PR by `scripts/versioning/bump.py` (tests:
`python3 -m unittest discover -s scripts/versioning -v`). That script is the **shared** versioning code for this repo,
hangarscope and multi-box (see "Shared versioning" below), so the rules are the same in all three:

1. A label wins: `semver:major`, `semver:minor` or `semver:patch` (with several labels, the biggest wins).
2. Otherwise, a PR that adds an entry to `whats-new/features.json` is **minor**. A visible *fix* also gets an
   announcement entry, so label it `semver:patch`.
3. Everything else is **patch**: fixes, refactors, CI, dependency bumps (Dependabot included), docs, process.
4. Major is never automatic.
5. With no published `vX.Y.Z` tag, the first release is `1.0.0` (already past here).
6. `deploy:manual` on any PR in a release publishes it as a **prerelease** (see Deploy).

**No skip.** A docs-only PR still gets a patch release. PRs are squash-merged, so each PR is one commit on `main`, one
version and one release-log entry. There are no bookkeeping commits: the version lives only in the tag and is
passed to the build as `-p:Version` (the csproj says `0.0.0-dev`). Old `Release vX.Y.Z` commits from the retired
`release.ps1` are ignored by the bump script.

**Exceptions (only these two):** when one release run covers **several** merges (a failed build fixed by the next PR,
or merges arriving faster than builds, see "How it runs"), they share one version with the biggest bump among them,
and the Release notes list every PR. And **backlog recovery**, if merges ever go unreleased: run the Release workflow
by hand with a forced bump (below). Both break the one-to-one mapping; releasing every merge as it lands avoids it.

| Label | Meaning |
| --- | --- |
| `semver:major` | Breaking change (e.g. a DB migration that can't run on an old cache, a dropped setting) |
| `semver:minor` | New feature, even without an announcement entry |
| `semver:patch` | Force patch (visible fix, or a typo fix in the announcement file) |
| `deploy:manual` | Release normally, but publish as a **prerelease** so installed copies don't update until it is promoted (see Deploy) |

Create missing labels once:

```bash
gh label create semver:major --color B60205 --description "Breaking change"
gh label create semver:minor --color 0E8A16 --description "New feature"
gh label create semver:patch --color C2E0C6 --description "Force patch bump"
gh label create deploy:manual --color FBCA04 --description "Release as prerelease; promote by hand to deploy"
```

## How it runs (automatic)

Merging is releasing. Every push to `main` runs `.github/workflows/release.yml`, one run at a time in merge order:

1. **version** (Linux): the shared `next-version` action (`.github/actions/next-version`, running
   `scripts/versioning/bump.py release`) finds the newest *published* `vX.Y.Z` tag in the commit's history (its
   GitHub Release is not a draft and has the Velopack `-full.nupkg`), looks up the merged PR of every first-parent
   commit since it (`gh api repos/<repo>/commits/<sha>/pulls`, for labels and title; a failed lookup fails the run),
   and picks the version and `prerelease`. It outputs `release` (build it) and `finalize` (write the notes).
   - The commit already has a `vX.Y.Z` tag (a re-run): that tag is reused. If its Release is already published,
     nothing is built (`release=false`) and only the notes are re-applied. If a newer published release already
     contains the commit, the run does nothing.
   - The commit is already inside a later tag (a newer run released it, or is releasing it): no release. Older code
     is never published as the newest version.
   - Otherwise one release for everything since the newest published tag. Normally that is exactly the one merged
     PR. A tag left by a failed run (no packages) is stepped over: the new version is numbered above every existing
     tag and its commits are in the notes.
2. **build** (Windows, the reusable `build-windows-app.yml` from the same commit): tests, `dotnet publish
   -p:Version=X.Y.Z`, Velopack pack, **then** creates the tag at the built commit, publishes the Velopack Release (a
   prerelease with `deploy:manual`) and attaches the zip. Nothing is tagged or published if the tests, publish or
   pack fail, and it refuses a version below an already released one.
3. **finalize**: the shared `finalize-release` action (`scripts/versioning/finalize.py`) sets the Release body (each
   new `whats-new/features.json` entry, the release-log entries added since the previous release with their index
   summaries, every PR, and the compare link), then fails the run if the Release is a draft or lacks the Velopack
   packages or the zip, and warns if its prerelease state doesn't match the labels.

GitHub keeps only the newest *pending* run per concurrency group. If three PRs merge while one build is running, the
middle run is dropped, and the last run releases both remaining merges as one version. Nothing is lost. A re-run
of an *old* run also joins that queue and can drop the pending run of a newer merge, which is why the runbook below
says to dispatch on `main` instead once `main` has moved on.

**Before merging:** the PR's "PR checks" run summary says `Merging this PR would release vX.Y.Z` and why. Wrong bump?
Add a `semver:*` label; the check re-runs on label changes. The same workflow validates `whats-new/features.json`
(the shared `validate-features` action, `scripts/versioning/validate_features.py`: valid JSON, required fields, unique
`pr`, images exist, entries added by this PR use its number), fails a PR that adds no new `changelog/YYYY-MM-DD-<name>/README.md`
(`scripts/versioning/check_changelog.py`; Dependabot PRs, author `dependabot[bot]`, are exempt and still release as a
patch), lints every workflow with a pinned, checksum-verified actionlint, and runs the `scripts/versioning` tests. The
preview is the shared `next-version` action in `pr` mode: the PR on top of any unreleased merges already on `main`,
numbered exactly as the release run would number it.
Each check runs even if an earlier one failed, so the summary is always there. Don't merge with "PR checks" red. A
broken `features.json` on `main` makes the bump script treat the file as unknown (patch, no announcement notes) until
it is fixed. The repo has no required checks today; making `release-checks` and `build-test` required in branch
protection would enforce this.

**After merging:** watch the run and check the result:

```bash
# the run can take a few seconds to appear; don't call `gh run watch` with an empty id (it goes interactive)
sha=$(git rev-parse origin/main)
for i in $(seq 1 12); do
  id=$(gh run list --workflow release.yml --commit "$sha" --limit 1 --json databaseId -q '.[0].databaseId')
  [ -n "$id" ] && break; sleep 5
done
[ -n "$id" ] && gh run watch "$id" --exit-status
gh release view vX.Y.Z    # must list Setup.exe, the .nupkg files and the zip
```

**By hand** (backlog recovery, or forcing a bump after the fact): Actions → Release → Run workflow on `main`, or
`gh workflow run release.yml --ref main -f bump=minor`. `auto` applies the normal rules. A forced bump applies to
everything since the newest published tag. If `main`'s tip is already released, the run does nothing.

### If the release run fails

The tag is created only after the artefact is built, so most failures leave **no tag and no Release**. First check
whether `main` has moved on since the failed commit (`git fetch && git log --oneline <sha>..origin/main`):

- **`main` has moved on:** don't re-run the old run. A re-run joins the release queue, can drop a newer merge's
  pending run, and would build an older version after a newer one. The newer merge's run (or, if that already
  finished, `gh workflow run release.yml --ref main`) releases everything since the last *published* tag, including
  the failed commit, numbered above any tag the failed run left behind.
- **The failed commit is still `main`'s tip:**
  - *Before the tag* (tests, publish, vpk pack): flaky (runner, network, quota)? `gh run rerun <id> --failed`. A
    code failure? Fix it in a `fix/` PR; that merge's run releases the failed PR and the fix together under one
    version, and the notes list both.
  - *After the tag, before the Velopack upload finished:* `gh run rerun <id> --failed`. The build reuses the tag
    (and refuses one pointing at a different commit).
  - *After the Velopack upload* (the Release has `Setup.exe`/`.nupkg`): the version has shipped. Never delete it.
    `gh run rerun <id> --failed` re-runs the zip upload or the notes; the build skips the Velopack upload it already
    did. A full re-run doesn't rebuild a shipped version; it re-applies the notes and fails if the zip is missing.
  - *version failed* (e.g. the GitHub API was down): `gh run rerun <id>`.
- A shipped version whose zip can't be attached any more: attach it by hand or leave it; the installer and update
  packages are what matter.

## Deploy

Installed copies poll GitHub Releases on startup and every 6 h (Velopack, stable releases only), so **publishing
the Release is the deploy**, and since every merge to `main` releases, **merging is deploying**. With
`deploy:manual` the Release is a **prerelease**: tagged and downloadable, but no installed copy updates
(`GithubSource(..., prerelease: false)` in `UpdateService.cs`). Say in the PR what the manual step is (for example a
data or SSO-scope change), do it, then promote: `gh release edit vX.Y.Z --prerelease=false`. Merging another PR
before promoting releases a newer, normal version on top that includes the held change, so don't, or label it
`deploy:manual` too. Rollback means releasing the previous code as a new, higher version (revert PR; installed copies
only pick up versions newer than their own).

## Shared versioning

The versioning code lives here once and the three apps call it; none of them carries a copy:

| Piece | Code | Used by |
| --- | --- | --- |
| `.github/actions/next-version` | `scripts/versioning/bump.py` | release runs (`mode: release`) and PR previews (`mode: pr`) |
| `.github/actions/finalize-release` | `scripts/versioning/finalize.py` | release runs: notes, completeness check, optional `CHANGELOG.md` / `<Version>` bookkeeping |
| `.github/actions/validate-features` | `scripts/versioning/validate_features.py` | PR CI: the announcement file |
| `.github/workflows/build-windows-app.yml` | | release runs and PR builds |

This repo uses its own copies (`./.github/actions/...`, `./.github/workflows/...`) from the same commit, so a change
ships in lockstep. hangarscope and multi-box use `apettey/eve-contracts/.github/actions/<name>@main`: the runner
downloads this repo at that ref, so the action always runs the scripts from the same commit. Per-repo differences are
action inputs, not code: `features-path`, `changelog-dir`, `app-name`, `bookkeeping-paths`, `notes-title`,
`release-log-notes` (`summary` or `full`), and on `finalize-release` `changelog-file`, `version-file` and `branch`.
The branch name and merge style come from the caller's workflow (it decides which pushes run).

**A change here changes three release pipelines.** Keep it backwards compatible (new inputs get defaults that keep
today's behaviour), cover it in `scripts/versioning/test_*.py`, and dry-run it against each repo before merging:

```bash
# from a checkout of the other repo, with this repo next to it; read-only (gh lookups only)
python3 ../eve-contracts/scripts/versioning/bump.py release --repo apettey/<repo> --head origin/<branch> \
  --features-path <path> --changelog-dir <dir> --app-name <app> [--bookkeeping-paths ...]
python3 ../eve-contracts/scripts/versioning/finalize.py --repo apettey/<repo> --tag vX.Y.Z \
  --notes-file notes.md [--changelog-file CHANGELOG.md --version-file <csproj>] --dry-run
```

`--offline` on `bump.py` skips GitHub altogether (every tag counts as published, no PR labels).

## Automate next (not in place yet)

- An in-app "What's New" reading `whats-new/features.json`, keyed by the highest PR number the user has seen.

`build-windows-app.yml` is shared with hangarscope and multi-box (`@main`), so changes to it affect those repos too.
Its `version`/`release-tag`/`prerelease` inputs are optional; callers that don't pass them keep the old tag-ref
behaviour.
