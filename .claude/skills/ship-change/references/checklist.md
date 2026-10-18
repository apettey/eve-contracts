# Checklist

```markdown
### Start
- [ ] Branched from latest main with --no-track (or stacked on the newest open PR): feature/|fix/|chore/<name>
- [ ] Visible change: "before" screenshot taken before the first edit (Windows dev app, 1600×1000)

### Build
- [ ] Tests added/updated (a failing test first for bug fixes)
- [ ] Code: `dotnet test tests/EveContracts.Tests/EveContracts.Tests.csproj -c Release` passes locally
- [ ] Code: App + Bench build; `dotnet publish ... -r win-x64` succeeds
- [ ] Code: tried in the running app, edge cases included (stubs noted). Non-code: output checked, CI green
- [ ] Visible change: "after" screenshot of the final version; README/docs/img updated if documented there

### Document
- [ ] changelog/YYYY-MM-DD-<name>/README.md (+ before/after), saying what was and wasn't run
- [ ] `python3 scripts/versioning/check_changelog.py --base origin/main` passes; workflow change: `actionlint` clean

### Draft PR
- [ ] Rebased on origin/main; index row at the top of changelog/README.md
- [ ] Draft PR opened; visible change: whats-new/features.json entry with pr = <N> + whats-new/img/pr-<N>-*.png
- [ ] PR body: what/why, before/after (SHA-pinned), release-log link, tests
- [ ] semver:* label if the default is wrong (visible fix = semver:patch); deploy:manual if a manual step is needed

### Review
- [ ] Fresh reviewer subagent given the diff, intent, PR number and checklist
- [ ] Every finding fixed or answered; re-reviewed on the new diff
- [ ] Repeated until a round had zero findings; round count in the PR body

### Release
- [ ] PR marked ready, CI green, "PR checks" says the expected vX.Y.Z, squash-merged
- [ ] Release run on main green: vX.Y.Z tagged, GitHub Release has installer + packages + zip
```
