# Review loop

The PR stays a **draft** and is not merged until a round comes back with **zero findings**. The loop runs after the
draft PR exists (SKILL.md step 7), so the announcement entry, index row and PR body are reviewed too. CI can't check
this; the round count in the PR body is the evidence.

## Procedure

1. Commit and push everything (code, tests, release-log entry, screenshots, announcement entry and image).
2. Spawn a **fresh** reviewer subagent with the `Agent` tool (`subagent_type: "general-purpose"`: it needs Bash for
   `git diff`). It must not be the agent that wrote the change. Use the brief below and keep the agent ID it returns.
3. For each finding: fix it, or write down why it isn't a problem. Commit.
4. Send the reviewer the **new** diff with `SendMessage` to the same agent ID (load it with `ToolSearch` `select:SendMessage` if
   deferred), listing what you fixed and what you rejected and why, and ask for another round. If that agent can't be resumed, spawn a
   new one with the full brief and the earlier findings.
5. Repeat until a round reports `NO FINDINGS`. Record `Review rounds: <n>, last one clean` in the PR body.

A finding you reject still counts. The round isn't clean until the reviewer accepts the explanation.

## Reviewer brief (paste into the subagent prompt; fill in the angle brackets)

```text
You are reviewing a change to the eve-contracts repo (the current checkout;
.NET 10 WPF + Blazor Hybrid Windows app for EVE Online contracts). You did not write it. Be strict and concrete.

Intent of the change: <what it is meant to do, and why>
Visible to users: <yes/no>
Release-log entry: changelog/<folder>/README.md
Draft PR: #<N> (read the body with: gh pr view <N>)

Get the diff with: git diff origin/main...HEAD   (and `git status` for anything uncommitted)
Read whatever surrounding code you need. Do not edit files.

Check:
- Correctness: does it do what the intent says, including edge cases (empty/missing/unknown data, many characters,
  ESI 304/400/403/404/504/empty-200, all-regions vs one region)? Any regression in nearby behaviour?
- Tests: is the new behaviour covered in tests/EveContracts.Tests? Would the tests fail without the change? Is a bug
  fix accompanied by a test that reproduces it?
- Evidence: does the release-log entry exist and match the diff? (Visible) are before.png/after.png present, and does
  after.png show the final UI? Is there an entry in whats-new/features.json with "pr": <N>, written for users and saying
  where to find the feature, with its image in whats-new/img/? Is the changelog/README.md index row at the top? Is
  README.md updated if it documents this screen? Does the PR body have before/after (SHA-pinned), tests and the
  release-log link? A visible fix carries the semver:patch label.
- Security: player-supplied text (contract titles, names, search input) rendered escaped, no MarkupString from it;
  tokens stay DPAPI-encrypted; no secrets committed; no new ESI/Fuzzwork/third-party calls unless stated in the
  release log.
- Repo rules: contract items fetched once per contract ever; ETags kept on list endpoints; no polling faster than
  ESI's cache window; pure logic in EveContracts.Core with Razor kept thin; filter path answers from the in-memory
  snapshot; bulk writes via the prepared-SQLite path; Bench still builds against Core.
- Scope: one change; no unrelated reformatting or drive-by edits.

Report each finding as:
  <file>:<line> — what's wrong — why it matters
If there is nothing to report, reply with exactly: NO FINDINGS
On later rounds, first say whether each earlier finding is resolved, then list any new ones.
```
