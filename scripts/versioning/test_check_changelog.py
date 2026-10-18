"""Tests for check_changelog.py. Run: python3 -m unittest discover -s scripts/versioning -v"""

import contextlib
import io
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from check_changelog import check, main  # noqa: E402

INDEX = "changelog/README.md"
OLD = ["changelog/2026-10-18-ship-change-skill/README.md",
       "changelog/2026-10-18-ship-change-skill/before.png", INDEX]


def entry(name, *extra):
    return [f"changelog/{name}/README.md", *(f"changelog/{name}/{x}" for x in extra)]


class Check(unittest.TestCase):
    def assertOk(self, base, head, **kw):
        self.assertEqual(check(base, head, **kw), [])

    def assertError(self, base, head, fragment, **kw):
        errors = check(base, head, **kw)
        self.assertTrue(any(fragment in e for e in errors), f"{fragment!r} not in {errors}")

    def test_new_entry_passes(self):
        self.assertOk(OLD, OLD + entry("2026-10-19-export-csv", "before.png", "after.png"))

    def test_no_change_fails(self):
        self.assertError(OLD, OLD, "no new release-log entry")

    def test_empty_trees_fail(self):
        self.assertError([], [], "no new release-log entry")

    def test_first_entry_ever_passes(self):
        self.assertOk([], entry("2026-10-19-first"))

    def test_editing_an_existing_entry_fails(self):
        # same paths, different content: the folder already existed at the base
        self.assertError(OLD, OLD + ["changelog/2026-10-18-ship-change-skill/after.png"],
                         "no new release-log entry")

    def test_readme_added_to_existing_folder_fails(self):
        base = ["changelog/2026-10-18-x/before.png"]
        self.assertError(base, base + entry("2026-10-18-x"), "no new release-log entry")

    def test_new_folder_without_readme(self):
        self.assertError(OLD, OLD + ["changelog/2026-10-19-x/after.png"], "has no README.md")

    def test_bad_folder_names(self):
        for bad in ("export-csv", "2026-10-19-Export-CSV", "2026-10-19_export", "2026-10-19-",
                    "2026-10-19-a--b", "26-10-19-x"):
            self.assertError(OLD, OLD + entry(bad), "name must be YYYY-MM-DD-<short-name>")

    def test_impossible_date(self):
        self.assertError(OLD, OLD + entry("2026-02-30-x"), "not a real date")

    def test_readme_must_be_directly_in_folder(self):
        self.assertError(OLD, OLD + ["changelog/2026-10-19-x/sub/README.md"], "has no README.md")

    def test_readme_name_is_case_sensitive(self):
        self.assertError(OLD, OLD + ["changelog/2026-10-19-x/readme.md"], "has no README.md")

    def test_one_good_entry_is_enough(self):
        self.assertOk(OLD, OLD + entry("bad name") + entry("2026-10-19-good"))

    def test_entry_removed_and_another_added_passes(self):
        self.assertOk(OLD, [INDEX] + entry("2026-10-19-new"))

    def test_dependabot_is_exempt(self):
        self.assertOk(OLD, OLD, author="dependabot[bot]")

    def test_other_bots_and_humans_are_not_exempt(self):
        for who in ("dependabot", "renovate[bot]", "apettey", "github-actions[bot]"):
            self.assertError(OLD, OLD, "no new release-log entry", author=who)


class Cli(unittest.TestCase):
    """main() against a real throwaway repo with a PR-style merge commit."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.old_cwd = os.getcwd()
        os.chdir(self.tmp.name)
        for args in (["init", "-q", "-b", "main"], ["config", "user.email", "t@example.com"],
                     ["config", "user.name", "T"], ["config", "commit.gpgsign", "false"]):
            subprocess.run(["git", *args], check=True)
        self.commit("initial", {INDEX: "# Release log\n", "changelog/2026-10-18-old/README.md": "old"})

    def tearDown(self):
        os.chdir(self.old_cwd)
        self.tmp.cleanup()

    def commit(self, msg, files):
        for path, text in files.items():
            os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
            with open(path, "w") as f:
                f.write(text)
        subprocess.run(["git", "add", "-A"], check=True)
        subprocess.run(["git", "commit", "-q", "--allow-empty", "-m", msg], check=True)

    def pr_merge(self, files):
        """Like actions/checkout on a PR: HEAD is the PR branch merged into main (HEAD^1)."""
        subprocess.run(["git", "switch", "-q", "-c", "pr"], check=True)
        self.commit("pr", files)
        subprocess.run(["git", "switch", "-q", "main"], check=True)
        self.commit("main moved", {"other.txt": "x"})
        subprocess.run(["git", "merge", "-q", "--no-ff", "-m", "merge", "pr"], check=True)

    def run_main(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = main(list(args))
        return code, out.getvalue(), err.getvalue()

    def test_pr_with_entry_passes(self):
        self.pr_merge({"changelog/2026-10-19-new/README.md": "# New", INDEX: "# Release log\nrow\n"})
        code, out, _ = self.run_main()
        self.assertEqual(code, 0)
        self.assertIn("OK", out)

    def test_pr_without_entry_fails(self):
        self.pr_merge({"src/a.cs": "1", "changelog/2026-10-18-old/README.md": "edited"})
        code, _, err = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("no new release-log entry", err)

    def test_entry_already_on_main_does_not_count(self):
        # main gained an entry after the PR branched; the PR itself adds none
        subprocess.run(["git", "switch", "-q", "-c", "pr"], check=True)
        self.commit("pr", {"src/a.cs": "1"})
        subprocess.run(["git", "switch", "-q", "main"], check=True)
        self.commit("other PR", {"changelog/2026-10-19-other/README.md": "x"})
        subprocess.run(["git", "merge", "-q", "--no-ff", "-m", "merge", "pr"], check=True)
        self.assertEqual(self.run_main()[0], 1)

    def test_dependabot_pr_without_entry_passes(self):
        self.pr_merge({"global.json": "{}"})
        code, out, _ = self.run_main("--author", "dependabot[bot]")
        self.assertEqual(code, 0)
        self.assertIn("exempt", out)

    def test_explicit_range(self):
        self.commit("entry", {"changelog/2026-10-19-new/README.md": "# New"})
        self.assertEqual(self.run_main("--base", "HEAD~1", "--head", "HEAD")[0], 0)
        self.assertEqual(self.run_main("--base", "HEAD", "--head", "HEAD")[0], 1)

    def test_bad_revision_fails_cleanly(self):
        code, _, err = self.run_main("--base", "no-such-rev")
        self.assertEqual(code, 1)
        self.assertIn("no-such-rev", err)


if __name__ == "__main__":
    unittest.main()
