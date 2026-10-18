# Shared build refuses to tag a stale version

**Date:** 2026-10-18

## Description

A re-run of an old release run (for example "Re-run failed jobs" on its build) keeps the version it computed back then.
If a newer release with a bigger bump shipped in between, the shared build would tag and publish the older code as a
new GitHub "Latest". Now `release-tag` builds refuse to create a tag below the highest existing `vX.Y.Z`.

## Change

- `.github/workflows/build-windows-app.yml`, step "Create tag (release-tag builds)": before creating a new tag, it
  lists the repo's tags. If any `vX.Y.Z` is higher than the requested one, the step fails and says to start a fresh
  release run from the latest commit. Nothing is tagged or published, and the tag cleanup step has nothing to remove.
- These are unaffected: a re-run whose tag already points at this commit (finishing that same version), the same
  version on another commit (already refused), tag-ref builds, and branch CI builds.
- Covers every caller: eve-contracts, hangarscope and multi-box.
- Tested: actionlint; the comparison against apettey/multi-box's real tags (v1.0.3 refused while v1.0.4 exists;
  v1.0.5 and v1.1.0 allowed). Not run: a stale re-run on GitHub.
