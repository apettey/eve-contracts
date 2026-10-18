# Prerelease input and tag cleanup for release-tag builds

**Date:** 2026-10-18

## Description

Two additions to the `release-tag` mode of `build-windows-app.yml`, for callers that release on every merge. A
`prerelease` input lets a caller hold the auto-update deploy. A failed publish no longer leaves a tag with no Release
behind it.

## Change

- New optional input `prerelease` (default `false`). It publishes the Velopack and zip Release as a prerelease, which
  stable-channel clients (`GithubSource(..., prerelease: false)`) don't update to; promote it with
  `gh release edit <tag> --prerelease=false`. Callers map their `deploy:manual` label to it.
- New step "Remove tag after failed publish". When a `release-tag` run created the tag and a later step fails before
  any Release is published, the step deletes any draft Release for that tag and then the tag. A caller's next run
  then releases those changes instead of treating the orphan tag as released. If a published Release exists, it
  keeps the tag. If it can't list releases, it fails and leaves the tag for a person to check.
- Without the new input, and on tag-ref or branch builds, behaviour is unchanged.
- First caller: apettey/multi-box `release.yml`.
- Tested: actionlint; the release-listing `--jq` filter against a real paginated listing. Not run: a prerelease or
  failed-publish build.
