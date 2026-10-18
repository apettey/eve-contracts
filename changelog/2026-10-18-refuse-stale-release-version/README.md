# Shared build refuses to publish a stale version

**Date:** 2026-10-18

## Description

A re-run of an old release run keeps the version it computed back then, and a caller may try to finish an old
orphaned tag. If a higher version was released in between, publishing would make older code GitHub's "Latest".
`vpk pack` usually stops this already, because it downloads the latest stable release and refuses a lower version.
It doesn't when the newer release is a prerelease (a `deploy:manual` hold) or that download failed. `release-tag`
builds now check explicitly.

## Change

- `.github/workflows/build-windows-app.yml`: new step "Refuse a stale version (release-tag builds)", before
  "Create tag". It fails if any published Release (prereleases included, drafts not) has a higher `vX.Y.Z` than the
  requested tag. Nothing is tagged or published, and the cleanup step has nothing to remove.
- It covers both creating a new tag and reusing a tag already on the commit (finishing an old orphaned tag after a
  newer release).
- Only published Releases count, so a stray tag with no Release (e.g. pushed from a branch) can't block releases.
- These are unaffected: finishing the newest version on the same commit, a first release, tag-ref builds and branch
  CI builds.
- Covers every caller: eve-contracts, hangarscope and multi-box.
- Tested: actionlint, and the comparison against apettey/multi-box's real releases (v1.0.3 refused while v1.0.4 is
  released; v1.0.4, v1.0.5 and v1.1.0 allowed). Not run: a stale run on GitHub.
