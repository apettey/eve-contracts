"""Tests for finalize.py. Run: python3 -m unittest discover -s scripts/versioning -v

The changelog cases come from multi-box's former Pester suite (Update-Changelog,
Set-ProjectVersion); the Release-state cases from hangarscope's former finalize job.
"""

import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
import io

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import finalize  # noqa: E402
from bump import ReleaseStatus  # noqa: E402
from finalize import demote, set_project_version, state_messages, update_changelog  # noqa: E402

LEGACY = """# Changelog

Intro.

## [Unreleased]

### Added

- Settings sync.

## [1.0.3] - 2026-09-24

### Added

- Version in header.
"""


class Changelog(unittest.TestCase):
    def test_inserts_above_the_newest_release_and_folds_unreleased_into_it(self):
        out = update_changelog(LEGACY, "1.1.0", "2026-10-18", "### Changes\n\n- A thing")
        self.assertNotIn("[Unreleased]", out)
        self.assertLess(out.index("## [1.1.0] - 2026-10-18"), out.index("## [1.0.3]"))
        self.assertLess(out.index("Intro."), out.index("## [1.1.0]"))
        self.assertIn("### Earlier, unversioned changes", out)
        self.assertIn("#### Added", out)
        self.assertIn("- Settings sync.", out)
        self.assertIn("- Version in header.", out)

    def test_without_an_unreleased_section(self):
        c = "# Changelog\n\n## [1.0.0] - 2026-01-01\n\n- First\n"
        out = update_changelog(c, "1.0.1", "2026-02-01", "- Fix")
        self.assertIn("## [1.0.1] - 2026-02-01\n\n- Fix\n\n## [1.0.0]", out)
        self.assertNotIn("Earlier, unversioned", out)

    def test_appends_when_there_is_no_release_yet(self):
        out = update_changelog("# Changelog\n", "1.0.0", "2026-02-01", "- First")
        self.assertEqual(out, "# Changelog\n\n## [1.0.0] - 2026-02-01\n\n- First\n")

    def test_idempotent_for_a_version_already_present(self):
        once = update_changelog(LEGACY, "1.1.0", "2026-10-18", "- A")
        self.assertEqual(update_changelog(once, "1.1.0", "2026-10-19", "- B"), once)

    def test_version_match_is_exact(self):
        once = update_changelog("# C\n\n## [1.1.0] - d\n", "1.1.0", "d", "- A")
        self.assertIn("## [1.1] - d", update_changelog(once, "1.1", "d", "- B"))  # 1.1 is not 1.1.0

    def test_keeps_crlf_line_endings(self):
        c = "# Changelog\r\n\r\n## [1.0.0] - 2026-01-01\r\n"
        out = update_changelog(c, "1.0.1", "2026-02-01", "- Fix")
        self.assertIn("## [1.0.1] - 2026-02-01\r\n", out)
        self.assertNotRegex(out, r"[^\r]\n")

    def test_demote_drops_the_title_and_nests_headings(self):
        notes = "# MultiBox v1.1.0\n\n## What's new\n\n### Thing\n\nText\n\n## Pull requests\n\n- X (#1)\n"
        self.assertEqual(demote(notes), "### What's new\n\n#### Thing\n\nText\n\n### Pull requests\n\n- X (#1)")
        self.assertEqual(demote("## Changes\n\n- a"), "### Changes\n\n- a")


class ProjectVersion(unittest.TestCase):
    def test_replaces_the_version(self):
        self.assertEqual(
            set_project_version("<Project><PropertyGroup><Version>1.0.3</Version></PropertyGroup></Project>", "1.1.0"),
            "<Project><PropertyGroup><Version>1.1.0</Version></PropertyGroup></Project>")

    def test_never_lowers_it(self):
        c = "<Version>2.0.0</Version>"
        self.assertEqual(set_project_version(c, "1.9.9"), c)
        self.assertEqual(set_project_version(c, "2.0.0"), c)

    def test_replaces_a_non_semver_placeholder(self):
        self.assertEqual(set_project_version("<Version>0.0.0-dev</Version>", "1.0.0"), "<Version>1.0.0</Version>")

    def test_throws_without_a_version_property(self):
        with self.assertRaises(ValueError):
            set_project_version("<Project/>", "1.1.0")

    def test_only_the_first_version_property(self):
        c = '<Version>1.0.0</Version><PackageReference Version="2.0.0" /><Version>9</Version>'
        self.assertEqual(set_project_version(c, "1.0.1"),
                         '<Version>1.0.1</Version><PackageReference Version="2.0.0" /><Version>9</Version>')


class ReleaseState(unittest.TestCase):
    def st(self, **kw):
        base = dict(exists=True, draft=False, prerelease=False, nupkg=True, zip=True)
        base.update(kw)
        return ReleaseStatus(**base)

    def test_complete_normal_release(self):
        summary, warnings, errors, shipped = state_messages("v1.0.0", self.st(), False, "success")
        self.assertIn("normal release", summary)
        self.assertEqual((warnings, errors, shipped), ([], [], True))

    def test_prerelease_summary_says_how_to_promote(self):
        summary, warnings, _, _ = state_messages("v1.0.0", self.st(prerelease=True), True, "success")
        self.assertIn("gh release edit v1.0.0 --prerelease=false", summary)
        self.assertEqual(warnings, [])

    def test_prerelease_without_the_label_warns(self):
        _, warnings, _, _ = state_messages("v1.0.0", self.st(prerelease=True), False, "skipped")
        self.assertIn("no PR in it has deploy:manual", warnings[0])

    def test_promoted_hold_is_quiet_on_a_notes_only_run(self):
        self.assertEqual(state_messages("v1.0.0", self.st(), True, "skipped")[1], [])

    def test_hold_that_was_already_normal_warns_after_a_build(self):
        _, warnings, _, _ = state_messages("v1.0.0", self.st(), True, "success")
        self.assertIn("already a normal release", warnings[0])

    def test_draft_or_no_packages_has_not_shipped(self):
        for st in (self.st(draft=True), self.st(nupkg=False), ReleaseStatus()):
            _, _, errors, shipped = state_messages("v1.0.0", st, False, "success")
            self.assertFalse(shipped)
            self.assertEqual(len(errors), 1)

    def test_missing_zip_has_shipped_but_fails(self):
        _, _, errors, shipped = state_messages("v1.0.0", self.st(zip=False), False, "success")
        self.assertTrue(shipped)
        self.assertIn("no zip", errors[0])


class DryRun(unittest.TestCase):
    def test_dry_run_prints_notes_and_the_new_changelog_without_changing_files(self):
        with tempfile.TemporaryDirectory() as d:
            old = os.getcwd()
            os.chdir(d)
            try:
                with open("CHANGELOG.md", "w") as f:
                    f.write(LEGACY)
                with open("App.csproj", "w") as f:
                    f.write("<Version>1.0.3</Version>")
                with open("notes.md", "w") as f:
                    f.write("## Pull requests\n\n- X (#1)\n")
                out = io.StringIO()
                with redirect_stdout(out):
                    rc = finalize.main(["--repo", "o/r", "--tag", "v1.1.0", "--notes-file", "notes.md",
                                        "--changelog-file", "CHANGELOG.md", "--version-file", "App.csproj",
                                        "--dry-run"])
                self.assertEqual(rc, 0)
                text = out.getvalue()
                self.assertIn("release notes for v1.1.0", text)
                self.assertIn("### Pull requests", text)
                self.assertIn("<Version>1.1.0</Version>", text)
                with open("CHANGELOG.md") as f:
                    self.assertEqual(f.read(), LEGACY)
            finally:
                os.chdir(old)


class Bookkeeping(unittest.TestCase):
    """The bookkeeping commit against a real bare 'origin'."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.old = os.getcwd()
        root = self.tmp.name
        g = lambda *a, cwd=root: subprocess.run(["git", *a], cwd=cwd, check=True, capture_output=True)
        g("init", "-q", "--bare", "-b", "main", "origin.git")
        g("clone", "-q", "origin.git", "work")
        self.work = os.path.join(root, "work")
        for args in (["config", "user.email", "t@example.com"], ["config", "user.name", "T"],
                     ["config", "commit.gpgsign", "false"]):
            g(*args, cwd=self.work)
        os.chdir(self.work)
        os.makedirs("src")
        with open("CHANGELOG.md", "w") as f:
            f.write(LEGACY)
        with open("src/App.csproj", "w") as f:
            f.write("<Version>1.0.3</Version>")
        g("add", "-A", cwd=self.work)
        g("commit", "-q", "-m", "init", cwd=self.work)
        g("push", "-q", "origin", "HEAD:main", cwd=self.work)

    def tearDown(self):
        os.chdir(self.old)
        self.tmp.cleanup()

    def test_commits_and_pushes_once(self):
        with redirect_stdout(io.StringIO()):
            finalize.bookkeeping("v1.1.0", "## Pull requests\n\n- X (#1)\n", "main",
                                 "CHANGELOG.md", "src/App.csproj", False)
            finalize.bookkeeping("v1.1.0", "## Pull requests\n\n- X (#1)\n", "main",
                                 "CHANGELOG.md", "src/App.csproj", False)  # idempotent re-run
        log = subprocess.run(["git", "log", "--format=%s", "origin/main"], capture_output=True, text=True).stdout
        self.assertEqual(log.splitlines(), ["Release v1.1.0: changelog and version", "init"])
        show = subprocess.run(["git", "show", "origin/main:src/App.csproj"], capture_output=True, text=True).stdout
        self.assertEqual(show, "<Version>1.1.0</Version>")


if __name__ == "__main__":
    unittest.main()
