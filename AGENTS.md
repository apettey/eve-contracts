# AGENTS.md — working on eve-contracts

Guidance for AI agents (and humans) contributing to this repository.

## Project facts

- Windows desktop app: .NET 10, WPF + Blazor Hybrid (WebView2). Building, testing and running need Windows.
- `src/EveContracts.Core` holds domain logic, the SQLite cache, the ESI client, the SDE loader and evaluation. `src/EveContracts.App` holds the UI.
  Tests are in `tests/EveContracts.Tests` and the perf harness in `tests/EveContracts.Bench`. See `README.md` and `OPTIMIZATIONS.md`.
- `EVECONTRACTS_DATA_DIR` points a dev build at a copy of the data, so the installed app's data is left alone.

## Shipping changes

Every change follows the **`ship-change`** skill (`.claude/skills/ship-change/SKILL.md`). These six rules are blocking:

1. Tests are in place, and the full suite passes locally (code changes; needs Windows) and in CI. PR CI also lints the workflows with actionlint.
2. A fresh reviewer reviews the diff, and the loop repeats until a round has zero findings. The round count goes in the PR body.
3. Every visible change has before/after screenshots in its release-log entry and in the PR.
4. Every change gets a `changelog/` entry. Visible changes also get a `whats-new/features.json` entry.
   PR CI fails a PR that adds no new `changelog/YYYY-MM-DD-<name>/README.md`. Only Dependabot PRs (author `dependabot[bot]`) are exempt.
5. Every merged PR gets a new SemVer version. There is no skip. It is automatic: merging to `main` runs the Release workflow, which tags and publishes the version only after the build succeeds. Merging is releasing (and deploying). A `deploy:manual` label publishes a prerelease that installed copies ignore until it is promoted. The versioning code (`scripts/versioning/`, `.github/actions/`) is shared with hangarscope and multi-box, which call it from here: a change to it changes their releases too.
6. Release quickly: small PRs, merged the same day, released right after merge.

If this file and the skill disagree, this file wins. Update both in the same PR.
