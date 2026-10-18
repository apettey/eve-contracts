# Versioning and release

## Every merged PR is a release

SemVer `MAJOR.MINOR.PATCH`, tag `vX.Y.Z`, decided per merged PR:

1. A label wins: `semver:major`, `semver:minor` or `semver:patch` (with several labels, the biggest wins).
2. Otherwise, a PR that adds an entry to `whats-new/features.json` is **minor**. A visible *fix* also gets an
   announcement entry, so label it `semver:patch`.
3. Everything else is **patch**: fixes, refactors, CI, dependency bumps (Dependabot included), docs, process.
4. Major is never automatic.

**No skip.** A docs-only PR still gets a patch release. PRs are squash-merged, so each PR is one commit on `main`, one
version and one release-log entry. The only other commits are `release.ps1`'s own `Release vX.Y.Z` bookkeeping
commits.

**Exceptions (only these two):** a failed tag build that is re-released under the same version after a fix (see
"If the tag build fails"), and **backlog recovery**: if PRs merged without a release (as everything in `git log v1.0.0..main` did), cut
one catch-up release with the biggest bump among them and list every PR and direct commit from
`git log <last-tag>..main` in the Release notes. That breaks the
one-to-one mapping once. The way to avoid it is to release right after every merge.

| Label | Meaning |
| --- | --- |
| `semver:major` | Breaking change (e.g. a DB migration that can't run on an old cache, a dropped setting) |
| `semver:minor` | New feature, even without an announcement entry |
| `semver:patch` | Force patch (visible fix, or a typo fix in the announcement file) |
| `deploy:manual` | Merge now, but a manual step must happen before the release goes out (see Deploy) |

Create missing labels once:

```bash
gh label create semver:major --color B60205 --description "Breaking change"
gh label create semver:minor --color 0E8A16 --description "New feature"
gh label create semver:patch --color C2E0C6 --description "Force patch bump"
gh label create deploy:manual --color FBCA04 --description "Manual step before release goes out"
```

## Releasing now (manual, until automated)

Right after the merge, on Windows:

```powershell
git switch main; git pull --ff-only
.\scripts\release.ps1 minor           # or patch / major / x.y.z
```

The script bumps `<Version>` in the App csproj, runs the tests, commits `Release vX.Y.Z`, tags it, and pushes `main`
and the tag together. That starts **two** Release runs: a CI build for the `main` push (`0.0.1-ci.N`) and the real
one for the tag. Don't use `-Watch`, because it follows whichever run started last. Watch the tag's run instead:

```bash
# the tag's run can take a few seconds to appear; don't call `gh run watch` with an empty id (it goes interactive)
for i in $(seq 1 12); do
  id=$(gh run list --workflow release.yml --branch vX.Y.Z --limit 1 --json databaseId -q '.[0].databaseId')
  [ -n "$id" ] && break; sleep 5
done
[ -n "$id" ] && gh run watch "$id" --exit-status
gh release view vX.Y.Z    # must list Setup.exe, the .nupkg and the zip
```

Without pwsh (macOS/Linux), do the same by hand: edit `<Version>`, then `git commit -m "Release vX.Y.Z"`,
`git tag vX.Y.Z` and `git push origin main vX.Y.Z`. CI runs the tests in the tag build.

Release notes: put the PR's `whats-new/features.json` entry (title and description) and the release-log link in the
Release body (`gh release edit vX.Y.Z --notes-file ...`).

### If the tag build fails

`release.ps1` tags **before** the artefact exists, unlike the target process, so a failed build leaves a tag that may
have no Release. First check how far the run got. `vpk upload github --publish` publishes the Velopack Release that
installed copies update from, and it runs *before* the zip upload.
- **The Velopack upload step succeeded** (the Release exists with `Setup.exe`/`.nupkg`): the version has shipped. Never
  delete it. Re-run the failed jobs (`gh run rerun <id> --failed`) to attach the zip. If that can't succeed, leave it
  and fix forward with the next version.
- **Flaky or infrastructure failure before the upload** (runner, network, quota): `gh run rerun <id> --failed`.
- **Code failure before the upload** (tests, publish, vpk pack): a re-run rebuilds the same commit and fails again.
  Delete the tag and any partial Release (`gh release delete vX.Y.Z --yes`, `git push --delete origin vX.Y.Z`,
  `git tag -d vX.Y.Z`). Fix the problem in a `fix/` PR, then release that merge as the same version with
  **`.\scripts\release.ps1 X.Y.Z`**. The csproj already says X.Y.Z, so the script skips the bump commit and just tags.
  A plain `patch`/`minor` would skip to the next number. This is the second exception to one PR per version: the
  failed PR and its fix share `vX.Y.Z`. Say so in the Release notes.

## Deploy

Installed copies poll GitHub Releases on startup and every 6 h (Velopack), so **publishing the Release is the
deploy**. There is no way to release without deploying today. `deploy:manual` therefore means: merge, do the manual
step (for example a data or SSO-scope change you must prepare first), then run the release the same day. Say what the step
is in the PR. Rollback means releasing the previous code as a new, higher version (installed copies only pick up
versions newer than their own).

## Automate next (not in place yet)

Until these exist, the manual steps above are mandatory:
- A tested bump script: inputs are the changed paths, latest tag and PR labels; outputs are `bump`, `version` and `reason`.
- A `push: main` release workflow (one run at a time, in merge order): compute version → build/pack → tag + Release
  only after the artefact is built. This also removes the failed-tag case above.
- PR CI: post "Merging this PR would release vX.Y.Z", validate `whats-new/features.json`, optionally fail a
  PR that adds no `changelog/` folder, lint workflows.
- An in-app "What's New" reading `whats-new/features.json`, keyed by the highest PR number the user has seen.

`build-windows-app.yml` is shared with hangarscope and multi-box (`@main`), so changes to it affect those repos too.
