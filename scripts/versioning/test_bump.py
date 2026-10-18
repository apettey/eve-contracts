"""Tests for bump.py. Run: python3 -m unittest discover -s scripts/versioning -v"""

import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bump  # noqa: E402
from bump import Decision, PullRequest, decide_bump, next_version, plan_release  # noqa: E402

F = bump.FEATURES_PATH


def features(*prs):
    return json.dumps({"features": [
        {"pr": n, "date": "2026-10-18", "title": f"Feature {n}", "description": f"Does {n}."} for n in prs]})


class DecideBump(unittest.TestCase):
    def test_default_is_patch(self):
        d = decide_bump(["src/EveContracts.App/Foo.cs"], [])
        self.assertEqual(d.bump, "patch")

    def test_no_paths_is_patch(self):
        self.assertEqual(decide_bump([], []).bump, "patch")

    def test_added_feature_entry_is_minor(self):
        d = decide_bump([F, "src/x.cs"], [], features(), features(42))
        self.assertEqual(d.bump, "minor")
        self.assertIn("#42", d.reason)

    def test_edited_feature_entry_is_patch(self):
        before = features(42)
        after = before.replace("Does 42.", "Does 42, better.")
        self.assertEqual(decide_bump([F], [], before, after).bump, "patch")

    def test_removed_feature_entry_is_patch(self):
        self.assertEqual(decide_bump([F], [], features(1, 2), features(1)).bump, "patch")

    def test_new_features_file_with_entry_is_minor(self):
        self.assertEqual(decide_bump([F], [], None, features(7)).bump, "minor")

    def test_features_content_ignored_when_path_not_changed(self):
        # contents passed but the PR didn't touch the file: not this PR's entry
        self.assertEqual(decide_bump(["README.md"], [], features(), features(1)).bump, "patch")

    def test_invalid_features_json_does_not_crash(self):
        self.assertEqual(decide_bump([F], [], "{", "{not json").bump, "patch")
        self.assertEqual(decide_bump([F], [], "[]", '{"features": 3}').bump, "patch")

    def test_repairing_broken_features_file_is_not_minor(self):
        # before was unparseable: every old entry would look "added"
        d = decide_bump([F], [], "{broken", features(1, 2, 3))
        self.assertEqual(d.bump, "patch")
        self.assertIn("invalid", d.reason)
        self.assertEqual(bump.new_features("{broken", features(1, 2, 3)), [])

    def test_bool_pr_is_not_an_entry(self):
        after = json.dumps({"features": [{"pr": True}]})
        self.assertEqual(decide_bump([F], [], features(), after).bump, "patch")

    def test_label_wins_over_announcement(self):
        d = decide_bump([F], ["semver:patch"], features(), features(42))
        self.assertEqual((d.bump, d.reason), ("patch", "label semver:patch"))

    def test_biggest_label_wins(self):
        self.assertEqual(decide_bump([], ["semver:patch", "semver:major", "semver:minor"]).bump, "major")
        self.assertEqual(decide_bump([], ["semver:patch", "semver:minor"]).bump, "minor")

    def test_label_case_and_whitespace(self):
        self.assertEqual(decide_bump([], [" SemVer:Minor "]).bump, "minor")

    def test_unrelated_and_malformed_labels_ignored(self):
        self.assertEqual(decide_bump([], ["bug", "semver:huge", "semver:", "deploy:manual"]).bump, "patch")

    def test_major_never_automatic(self):
        # even a huge PR that adds many announcements is only minor without the label
        d = decide_bump([F] + [f"src/{i}.cs" for i in range(500)], [], features(), features(1, 2, 3))
        self.assertEqual(d.bump, "minor")


class Versions(unittest.TestCase):
    def test_next_version(self):
        self.assertEqual(next_version((1, 2, 3), "patch"), (1, 2, 4))
        self.assertEqual(next_version((1, 2, 3), "minor"), (1, 3, 0))
        self.assertEqual(next_version((1, 2, 3), "major"), (2, 0, 0))

    def test_first_release_is_1_0_0_whatever_the_bump(self):
        for b in ("patch", "minor", "major"):
            self.assertEqual(next_version(None, b), (1, 0, 0))

    def test_unknown_bump_raises(self):
        with self.assertRaises(ValueError):
            next_version((1, 0, 0), "huge")

    def test_latest_version_is_semver_max_not_lexical(self):
        self.assertEqual(bump.latest_version(["v1.9.0", "v1.10.0", "v1.2.0"]), (1, 10, 0))

    def test_latest_version_ignores_prerelease_and_junk(self):
        self.assertEqual(bump.latest_version(["v2.0.0-rc.1", "v1.0.0", "1.5.0", "vX", "v1.0"]), (1, 0, 0))
        self.assertIsNone(bump.latest_version([]))
        self.assertIsNone(bump.latest_version(["v2.0.0-rc.1"]))


class NextCli(unittest.TestCase):
    def run_cli(self, *args, stdin=""):
        out = io.StringIO()
        old = sys.stdin
        sys.stdin = io.StringIO(stdin)
        try:
            with redirect_stdout(out):
                self.assertEqual(bump.main(list(args)), 0)
        finally:
            sys.stdin = old
        return dict(l.split("=", 1) for l in out.getvalue().splitlines())

    def test_v1_0_0_exists_so_next_patch_is_1_0_1(self):
        o = self.run_cli("next", "--latest-tag", "v1.0.0", stdin="docs/a.md\n")
        self.assertEqual((o["bump"], o["version"]), ("patch", "1.0.1"))

    def test_no_tag_is_1_0_0(self):
        self.assertEqual(self.run_cli("next", "--labels", "semver:minor")["version"], "1.0.0")

    def test_labels_and_features_files(self):
        with tempfile.TemporaryDirectory() as d:
            b, a, p = (os.path.join(d, n) for n in ("b.json", "a.json", "paths"))
            for path, text in ((b, features()), (a, features(9)), (p, F + "\n")):
                with open(path, "w") as f:
                    f.write(text)
            o = self.run_cli("next", "--latest-tag", "v1.4.2", "--paths-file", p,
                             "--features-before", b, "--features-after", a)
            self.assertEqual((o["bump"], o["version"]), ("minor", "1.5.0"))
            o = self.run_cli("next", "--latest-tag", "v1.4.2", "--paths-file", p, "--labels",
                             "bug,semver:major", "--features-before", b, "--features-after", a)
            self.assertEqual((o["bump"], o["version"]), ("major", "2.0.0"))

    def test_bad_latest_tag_is_an_error(self):
        with self.assertRaises(SystemExit), redirect_stdout(io.StringIO()):
            with open(os.devnull, "w") as null:
                old = sys.stderr
                sys.stderr = null
                try:
                    bump.main(["next", "--latest-tag", "1.0"])
                finally:
                    sys.stderr = old

    def test_multiline_output_uses_heredoc(self):
        out = io.StringIO()
        bump.emit({"a": "x", "notes": "l1\nl2\n"}, out)
        lines = out.getvalue().splitlines()
        self.assertEqual(lines[0], "a=x")
        self.assertRegex(lines[1], r"^notes<<EOF_NOTES_[0-9a-f]{16}$")
        self.assertEqual(lines[2:], ["l1", "l2", lines[1].split("<<")[1]])


class GitRepo(unittest.TestCase):
    """Range mode against a real throwaway repo; PR lookup is faked."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.old_cwd = os.getcwd()
        os.chdir(self.tmp.name)
        self.prs = {}
        for args in (["init", "-q", "-b", "main"], ["config", "user.email", "t@example.com"],
                     ["config", "user.name", "T"], ["config", "commit.gpgsign", "false"],
                     ["config", "tag.gpgsign", "false"]):
            subprocess.run(["git", *args], check=True)
        self.commit("initial", {"README.md": "hi"})

    def tearDown(self):
        os.chdir(self.old_cwd)
        self.tmp.cleanup()

    def commit(self, msg, files, pr=None, labels=()):
        for path, text in files.items():
            os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
            with open(path, "w") as f:
                f.write(text)
        subprocess.run(["git", "add", "-A"], check=True)
        subprocess.run(["git", "commit", "-q", "--allow-empty", "-m", msg], check=True)
        sha = bump.git("rev-parse", "HEAD").strip()
        if pr:
            self.prs[sha] = PullRequest(pr, msg, list(labels))
        return sha

    def tag(self, name, rev="HEAD", annotated=False):
        extra = ["-a", "-m", name] if annotated else []
        subprocess.run(["git", "tag", *extra, name, rev], check=True)

    def lookup(self, sha):
        return self.prs.get(sha)

    def test_no_tags_first_release_is_1_0_0(self):
        self.commit("feat", {"a.cs": "1"}, pr=1)
        p = plan_release("HEAD", self.lookup)
        self.assertFalse(p.skip)
        self.assertEqual(p.version, "1.0.0")
        self.assertIn("first release", p.reason)

    def test_one_pr_after_v1_0_0(self):
        self.tag("v1.0.0")
        self.commit("Fix thing", {"a.cs": "1"}, pr=20)
        p = plan_release("HEAD", self.lookup)
        self.assertEqual((p.bump, p.version, p.previous), ("patch", "1.0.1", "v1.0.0"))
        self.assertEqual(len(p.commits), 1)

    def test_announcement_entry_is_minor_and_in_notes(self):
        self.commit("add features file", {F: features()})
        self.tag("v1.0.0")
        self.commit("Export CSV", {F: features(21), "changelog/2026-10-18-export/README.md": "x"}, pr=21)
        p = plan_release("HEAD", self.lookup)
        self.assertEqual((p.bump, p.version), ("minor", "1.1.0"))
        notes = bump.release_notes(p, "o/r")
        self.assertIn("## Feature 21", notes)
        self.assertIn("Does 21.", notes)
        self.assertIn("- Export CSV (#21)", notes)
        self.assertIn("changelog/2026-10-18-export/README.md", notes)
        self.assertIn("compare/v1.0.0...v1.1.0", notes)

    def test_label_from_pr_lookup(self):
        self.tag("v1.0.0")
        self.commit("Breaking", {"a.cs": "1"}, pr=22, labels=["semver:major"])
        self.assertEqual(plan_release("HEAD", self.lookup).version, "2.0.0")

    def test_already_tagged_head_is_reused(self):
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=23)
        self.tag("v1.0.1")
        p = plan_release("HEAD", self.lookup)
        self.assertTrue(p.reused_tag)
        self.assertFalse(p.skip)
        self.assertEqual((p.version, p.previous), ("1.0.1", "v1.0.0"))
        self.assertEqual([c.pr.number for c in p.commits], [23])
        notes = bump.release_notes(p, "o/r")
        self.assertIn("- Fix (#23)", notes)
        self.assertNotIn("Bump:", notes)

    def test_commit_inside_a_newer_release_is_skipped(self):
        self.tag("v1.0.0")
        older = self.commit("A", {"a.cs": "1"}, pr=24)
        self.commit("B", {"b.cs": "1"}, pr=25)
        self.tag("v1.0.1")
        p = plan_release(older, self.lookup)
        self.assertTrue(p.skip)
        self.assertIn("already part of v1.0.1", p.reason)

    def test_cancelled_runs_roll_into_one_release_with_biggest_bump(self):
        self.commit("features", {F: features()})
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=26)
        self.commit("Feature", {F: features(27)}, pr=27)
        self.commit("Docs", {"d.md": "1"}, pr=28)
        p = plan_release("HEAD", self.lookup)
        self.assertEqual((p.bump, p.version), ("minor", "1.1.0"))
        self.assertEqual([c.pr.number for c in p.commits], [26, 27, 28])
        self.assertIn("3 commits", p.reason)

    def test_direct_push_without_pr_is_patch(self):
        self.tag("v1.0.0")
        sha = self.commit("tweak docs", {"d.md": "1"})
        p = plan_release("HEAD", self.lookup)
        self.assertEqual(p.version, "1.0.1")
        self.assertIn("direct push " + sha[:7], p.reason)
        self.assertIn(f"- tweak docs ({sha[:7]})", bump.release_notes(p, "o/r"))

    def test_legacy_release_commit_is_ignored(self):
        self.tag("v1.0.0")
        self.commit("Release v1.0.1", {"x.csproj": "<Version>1.0.1</Version>"})
        self.tag("v1.0.1")
        self.commit("Feature", {"a.cs": "1"}, pr=29, labels=["semver:minor"])
        p = plan_release("HEAD", self.lookup)
        self.assertEqual(p.version, "1.1.0")
        self.assertEqual(len(p.commits), 1)

    def test_only_legacy_commits_means_skip(self):
        self.tag("v1.0.0")
        self.commit("Release v1.0.1", {"x.csproj": "1"})
        self.assertTrue(plan_release("HEAD", self.lookup).skip)

    def test_force_bump_overrides(self):
        self.tag("v1.0.0")
        self.commit("Docs", {"d.md": "1"}, pr=30)
        p = plan_release("HEAD", self.lookup, force_bump="minor")
        self.assertEqual((p.version, p.reason), ("1.1.0", "manual override: minor"))

    def test_highest_tag_not_most_recent(self):
        self.tag("v1.10.0")
        self.tag("v1.9.0")
        self.commit("Fix", {"a.cs": "1"}, pr=31)
        self.assertEqual(plan_release("HEAD", self.lookup).version, "1.10.1")

    def test_tag_not_on_main_history_is_an_error(self):
        base = bump.git("rev-parse", "HEAD").strip()
        subprocess.run(["git", "switch", "-q", "-c", "side"], check=True)
        self.commit("side", {"s.cs": "1"})
        self.tag("v1.0.0")
        subprocess.run(["git", "switch", "-q", "main"], check=True)
        self.assertEqual(bump.git("rev-parse", "HEAD").strip(), base)
        self.commit("main work", {"m.cs": "1"}, pr=32)
        with self.assertRaises(RuntimeError):
            plan_release("HEAD", self.lookup)

    def test_annotated_tags(self):
        self.tag("v1.0.0", annotated=True)
        self.commit("Fix", {"a.cs": "1"}, pr=40)
        p = plan_release("HEAD", self.lookup)
        self.assertEqual((p.version, p.previous), ("1.0.1", "v1.0.0"))
        self.tag("v1.0.1", annotated=True)
        p = plan_release("HEAD", self.lookup)
        self.assertTrue(p.reused_tag)
        self.assertEqual(p.version, "1.0.1")

    def test_unpublished_tag_is_rolled_forward(self):
        # run for #41 tagged v1.0.1 then failed before publishing; #42's run must
        # release both, above the dangling tag, and list #41 in the notes
        self.commit("features", {F: features()})
        self.tag("v1.0.0")
        self.commit("Feature", {F: features(41)}, pr=41)
        self.tag("v1.1.0")
        self.commit("Fix", {"a.cs": "1"}, pr=42)
        p = plan_release("HEAD", self.lookup, published=lambda t: t != "v1.1.0")
        self.assertEqual((p.previous, p.bump, p.version), ("v1.0.0", "minor", "1.1.1"))
        self.assertEqual([c.pr.number for c in p.commits], [41, 42])
        self.assertIn("skips unpublished v1.1.0", p.reason)
        self.assertIn("## Feature 41", bump.release_notes(p, "o/r"))

    def test_unpublished_tag_rolled_forward_keeps_bigger_bump(self):
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=43)
        self.tag("v1.0.1")
        self.commit("Feature", {"b.cs": "1"}, pr=44, labels=["semver:minor"])
        p = plan_release("HEAD", self.lookup, published=lambda t: t != "v1.0.1")
        self.assertEqual(p.version, "1.1.0")  # above v1.0.1 already, no collision

    def test_rerun_of_superseded_tag_is_skipped(self):
        self.tag("v1.0.0")
        old = self.commit("Fix", {"a.cs": "1"}, pr=45)
        self.tag("v1.0.1")  # never published
        self.commit("Docs", {"d.md": "1"}, pr=46)
        self.tag("v1.0.2")  # the next run released both
        p = plan_release(old, self.lookup, published=lambda t: t != "v1.0.1")
        self.assertTrue(p.skip)
        self.assertIn("superseded by v1.0.2", p.reason)

    def test_rerun_of_unpublished_tag_reuses_it(self):
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=47)
        self.tag("v1.0.1")
        p = plan_release("HEAD", self.lookup, published=lambda t: t != "v1.0.1")
        self.assertTrue(p.reused_tag)
        self.assertEqual((p.version, p.previous), ("1.0.1", "v1.0.0"))

    def test_only_unpublished_tags(self):
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=48)
        p = plan_release("HEAD", self.lookup, published=lambda t: False)
        self.assertEqual(p.version, "1.0.1")
        self.assertEqual(len(p.commits), 2)  # whole history: nothing was ever published

    def test_pr_cli_previews_against_base(self):
        self.commit("features", {F: features()})
        self.tag("v1.2.0")
        base = bump.git("rev-parse", "HEAD").strip()
        self.commit("Feature", {F: features(33)})
        out = io.StringIO()
        with redirect_stdout(out):
            bump.main(["pr", "--base", base, "--head", "HEAD"])
        o = dict(l.split("=", 1) for l in out.getvalue().splitlines())
        self.assertEqual((o["bump"], o["version"], o["latest"]), ("minor", "1.3.0", "v1.2.0"))


if __name__ == "__main__":
    unittest.main()
