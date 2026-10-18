## What

<!-- What changed and why, in plain language. -->

## Screenshots

<!-- Visible changes only. Link raw URLs pinned to a commit SHA so they survive branch deletion. -->

| Before | After |
| --- | --- |
|  |  |

Release log: `changelog/YYYY-MM-DD-<name>/`

## Testing

<!-- N/N tests pass; what you ran by hand in the dev app; anything stubbed. -->

## Checklist

- [ ] Code change: tests added/updated; `dotnet test tests/EveContracts.Tests/EveContracts.Tests.csproj -c Release` passes locally. Non-code: CI green
- [ ] Release-log entry: `changelog/YYYY-MM-DD-<name>/` (with before/after if visible) + index row
- [ ] Visible to users? `whats-new/features.json` entry with this PR's number
- [ ] Review loop done: <N> rounds, last one with no findings
- [ ] Version: the "PR checks" summary says the expected `vX.Y.Z` (announcement = minor, everything else = patch; override with a `semver:*` label, visible fix = `semver:patch`). Merging releases it
- [ ] Code change: release artefact builds locally (`dotnet publish ... -r win-x64`)
