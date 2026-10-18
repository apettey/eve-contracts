# Formats

## Release-log entry

Folder: `changelog/YYYY-MM-DD-<short-name>/` (lower-case, hyphenated), with `README.md`, `before.png` and `after.png`
(more images if needed, e.g. `after-detail.png`).

```markdown
# <Change name>

**Date:** YYYY-MM-DD

## Description

One to three sentences: what changed and why.

## Before / After

| Before | After |
| --- | --- |
| ![before](before.png) | ![after](after.png) |

<!-- Nothing visible? Delete the table and describe the change under "Change" instead. -->

## Change

What is different now: files/modules added or changed, new settings, new ESI/Fuzzwork calls ("none"), DB schema
changes, tests added, anything stubbed while testing (e.g. "SSO stubbed; Own Contracts not exercised").
```

Keep it short; it is a log, not documentation (feature docs live in `README.md`).

The "PR checks" workflow fails a PR that adds no such folder: `scripts/versioning/check_changelog.py` needs a
`changelog/<folder>/README.md` whose folder is new in the PR, with a real date and a lower-case hyphenated name.
Editing an old entry doesn't count. Dependabot PRs (author `dependabot[bot]`) are exempt. Check locally after
committing (it reads commits, not the working tree): `python3 scripts/versioning/check_changelog.py --base origin/main`.

### Index row

Add at the **top** of the table in `changelog/README.md`, after rebasing on the latest `main`:

```markdown
| 2026-10-17 | [Export to CSV](2026-10-17-export-csv/README.md) | Export button on All Contracts downloads the current table as CSV |
```

Summaries of internal changes start with `Internal:`.

**Parallel PRs:** the index and `whats-new/features.json` both take a row at the top, so PRs branched in parallel
conflict there. Either stack the PR on the newest open PR's branch, or leave both shared files untouched until the
rebase right before `gh pr create`.

## Announcement entry (visible changes only)

`whats-new/features.json`, newest first:

```json
{
  "features": [
    {
      "pr": 42,
      "date": "2026-10-17",
      "title": "Export to CSV",
      "description": "All Contracts now has an Export button that downloads the table you're looking at as a CSV file.\n\nThe export uses your current search, filters and sort order.",
      "images": [
        { "src": "whats-new/img/pr-42-export-csv.png", "caption": "The Export button above the All Contracts table" }
      ]
    }
  ]
}
```

| Field | Rule |
| --- | --- |
| `pr` | Integer, unique: this PR's number (so open the PR first). |
| `date` | `YYYY-MM-DD` |
| `title` | The feature's name. |
| `description` | 1–3 sentences of plain text (no HTML), in the user's words: what it does and **where to find it**. `\n\n` starts a new paragraph. |
| `images` | Optional, 1–2 `{ src, caption }`; the caption says where in the app the screenshot is. |

Check it before pushing: `python3 scripts/versioning/validate_features.py` (the "PR checks" workflow runs the same
check, plus "entries added by this PR use its number").

## PR body

Use `.github/pull_request_template.md`. Fill in:
- **What:** what changed and why, in plain language.
- **Screenshots:** before/after images, linked to raw URLs **pinned to a commit SHA**:
  `https://raw.githubusercontent.com/apettey/eve-contracts/<sha>/changelog/<folder>/after.png`.
- Link to the release-log entry, a test summary (`N/N pass`, what was run by hand), and
  `Review rounds: <n>, last one clean`.
- Anything deliberately out of scope ("Not in this PR").
