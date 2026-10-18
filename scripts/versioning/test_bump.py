"""Tests for bump.py, the shared versioning engine. Run: python3 -m unittest discover -s scripts/versioning -v

Merged from the three repos' former suites: eve-contracts (test_bump.py), hangarscope
(scripts/tests/test_release_version.py) and multi-box (scripts/release/tests/*.Tests.ps1).
"""

import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bump  # noqa: E402
from bump import (Config, Decision, PullRequest, ReleaseStatus, decide_bump,  # noqa: E402
                  next_version, plan_preview, plan_release)

F = bump.Config.features_path
EC = Config()  # eve-contracts' layout (the defaults)
HS = Config(features_path="docs/whats-new/features.json", changelog_dir="docs/changelog",
            app_name="HangarScope", notes_title="{app} {tag}", release_log_notes="full")
MB = Config(bookkeeping_paths=("CHANGELOG.md", "src/App/App.csproj"), app_name="MultiBox")


def features(*prs):
    return json.dumps({"features": [
        {"pr": n, "date": "2026-10-18", "title": f"Feature {n}", "description": f"Does {n}."} for n in prs]})


PUBLISHED = ReleaseStatus(True, False, False, True, True)


def published_except(*unpublished):
    return lambda tag: ReleaseStatus() if tag in unpublished else PUBLISHED


EVERYTHING = published_except()


class DecideBump(unittest.TestCase):
    def test_default_is_patch(self):
        d = decide_bump(["src/EveContracts.App/Foo.cs"], [])
        self.assertEqual(d.bump, "patch")

    def test_no_paths_is_still_patch_never_none(self):
        self.assertEqual(decide_bump([], []).bump, "patch")

    def test_docs_and_ci_only_is_patch(self):
        self.assertEqual(decide_bump(["README.md", ".github/workflows/ci.yml"], []).bump, "patch")

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

    def test_new_features_file_without_entries_is_patch(self):
        self.assertEqual(decide_bump([F], [], None, features()).bump, "patch")

    def test_features_content_ignored_when_path_not_changed(self):
        # contents passed but the change didn't touch the file: not this change's entry
        self.assertEqual(decide_bump(["README.md"], [], features(), features(1)).bump, "patch")

    def test_features_path_is_configurable(self):
        hs = HS.features_path
        self.assertEqual(decide_bump([hs], [], features(), features(3), hs).bump, "minor")
        self.assertEqual(decide_bump([F], [], features(), features(3), hs).bump, "patch")

    def test_windows_separators_match(self):
        hs = HS.features_path
        self.assertEqual(decide_bump([hs.replace("/", "\\")], [], features(), features(3), hs).bump, "minor")

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

    def test_minor_label_without_announcement(self):
        self.assertEqual(decide_bump(["a.cs"], ["semver:minor"]).bump, "minor")

    def test_biggest_label_wins(self):
        self.assertEqual(decide_bump([], ["semver:patch", "semver:major", "semver:minor"]).bump, "major")
        self.assertEqual(decide_bump([], ["semver:patch", "semver:minor"]).bump, "minor")

    def test_label_case_and_whitespace(self):
        self.assertEqual(decide_bump([], [" SemVer:Minor "]).bump, "minor")

    def test_unrelated_and_malformed_labels_ignored(self):
        self.assertEqual(decide_bump([], ["bug", "semver:huge", "semver:", "deploy:manual"]).bump, "patch")

    def test_major_never_automatic(self):
        # even a huge change that adds many announcements is only minor without the label
        d = decide_bump([F] + [f"src/{i}.cs" for i in range(500)], [], features(), features(1, 2, 3))
        self.assertEqual(d.bump, "minor")

    def test_deploy_manual_is_prerelease(self):
        self.assertTrue(bump.is_prerelease(["bug", " Deploy:Manual "]))
        self.assertFalse(bump.is_prerelease(["semver:minor", "deploy:manualx"]))
        self.assertFalse(bump.is_prerelease([]))


class Bookkeeping(unittest.TestCase):
    def test_release_job_commit_is_bookkeeping(self):
        self.assertTrue(bump.is_bookkeeping("Release v1.1.0: changelog and version",
                                            ["CHANGELOG.md", "src/App/App.csproj"], MB))

    def test_touching_anything_else_is_a_real_change(self):
        self.assertFalse(bump.is_bookkeeping("Release v1.1.0: changelog and version",
                                             ["CHANGELOG.md", "src/a.cs"], MB))

    def test_ordinary_message_touching_the_changelog_is_a_real_change(self):
        self.assertFalse(bump.is_bookkeeping("Fix the changelog", ["CHANGELOG.md"], MB))
        self.assertFalse(bump.is_bookkeeping("Release v1.1.0 (#20)", ["CHANGELOG.md"], MB))

    def test_no_files_is_not_bookkeeping_when_paths_are_configured(self):
        self.assertFalse(bump.is_bookkeeping("Release v1.1.0: changelog and version", [], MB))

    def test_legacy_release_commits_without_configured_paths(self):
        self.assertTrue(bump.is_bookkeeping("Release v1.0.1", ["x.csproj"], EC))
        self.assertFalse(bump.is_bookkeeping("Release notes tweak", ["x.csproj"], EC))


class Versions(unittest.TestCase):
    def test_next_version(self):
        self.assertEqual(next_version((1, 2, 3), "patch"), (1, 2, 4))
        self.assertEqual(next_version((1, 2, 3), "minor"), (1, 3, 0))
        self.assertEqual(next_version((1, 2, 3), "major"), (2, 0, 0))

    def test_first_release_is_1_0_0_whatever_the_bump(self):
        for b in ("patch", "minor", "major"):
            self.assertEqual(next_version(None, b), (1, 0, 0))

    def test_number_version_steps_past_dangling_tags(self):
        nv = bump.number_version
        self.assertEqual(nv((1, 0, 0), (1, 0, 0), "minor"), (1, 1, 0))
        self.assertEqual(nv((1, 0, 0), (1, 1, 0), "minor"), (1, 1, 1))
        self.assertEqual(nv((1, 0, 0), (1, 0, 1), "minor"), (1, 1, 0))
        self.assertEqual(nv((1, 0, 0), (2, 0, 0), "major"), (2, 0, 1))  # no 3.0.0 over-bump
        self.assertEqual(nv((1, 0, 0), (2, 0, 0), "patch"), (2, 0, 1))
        self.assertEqual(nv(None, (1, 0, 0), "patch"), (1, 0, 1))
        self.assertEqual(nv(None, None, "minor"), (1, 0, 0))

    def test_unknown_bump_raises(self):
        with self.assertRaises(ValueError):
            next_version((1, 0, 0), "huge")
        with self.assertRaises(ValueError):
            next_version(None, "huge")

    def test_latest_version_is_semver_max_not_lexical(self):
        self.assertEqual(bump.latest_version(["v1.9.0", "v1.10.0", "v1.2.0"]), (1, 10, 0))

    def test_latest_version_ignores_prerelease_and_junk(self):
        self.assertEqual(bump.latest_version(["v2.0.0-rc.1", "v1.0.0", "1.5.0", "vX", "v1.0"]), (1, 0, 0))
        self.assertIsNone(bump.latest_version([]))
        self.assertIsNone(bump.latest_version(["v2.0.0-rc.1"]))


class GitHubData(unittest.TestCase):
    def test_pick_pr_prefers_the_pr_merged_as_this_commit(self):
        pulls = [{"number": 1, "title": "other", "merged_at": "x", "merge_commit_sha": "zzz", "labels": []},
                 {"number": 2, "title": "this", "merged_at": "x", "merge_commit_sha": "abc",
                  "labels": [{"name": "semver:minor"}]}]
        pr = bump.pick_pr("abc", pulls)
        self.assertEqual((pr.number, pr.labels), (2, ["semver:minor"]))

    def test_pick_pr_ignores_open_prs(self):
        self.assertIsNone(bump.pick_pr("abc", [{"number": 3, "title": "open", "merged_at": None}]))
        self.assertIsNone(bump.pick_pr("abc", []))

    def test_pick_pr_falls_back_to_a_merged_pr_containing_it(self):
        pr = bump.pick_pr("abc", [{"number": 4, "title": "rebased", "merged_at": "x", "merge_commit_sha": "zzz"}])
        self.assertEqual(pr.number, 4)

    def test_release_status(self):
        rel = {"draft": False, "prerelease": True, "assets": [
            {"name": "MultiBox-1.2.0-full.nupkg"}, {"name": "MultiBox-1.2.0-win-x64.zip"}]}
        st = bump.status_from_release(rel, "v1.2.0", MB)
        self.assertTrue(st.published and st.complete and st.prerelease)
        st = bump.status_from_release({"draft": True, "assets": rel["assets"]}, "v1.2.0", MB)
        self.assertFalse(st.published)
        st = bump.status_from_release({"assets": [{"name": "MultiBox-1.2.0-full.nupkg"}]}, "v1.2.0", MB)
        self.assertTrue(st.published)
        self.assertFalse(st.complete)
        # another version's zip doesn't count
        st = bump.status_from_release({"assets": [{"name": "x-full.nupkg"}, {"name": "MultiBox-1.1.0-win-x64.zip"}]},
                                      "v1.2.0", MB)
        self.assertFalse(st.zip)

    def test_zip_without_app_name(self):
        self.assertTrue(bump.zip_name_matches("ContractTracker-1.2.0-win-x64.zip", "1.2.0", EC))
        self.assertFalse(bump.zip_name_matches("ContractTracker-1.2.01-win-x64.zip", "1.2.0", EC))

    def test_failed_pr_lookup_fails_closed(self):
        old = subprocess.run
        subprocess.run = lambda *a, **k: subprocess.CompletedProcess(a, 1, "", "HTTP 502")
        try:
            with self.assertRaisesRegex(RuntimeError, "not guessing"):
                bump.gh_pr_lookup("o/r")("abc")
        finally:
            subprocess.run = old

    def test_release_status_404_is_unpublished_other_errors_fail(self):
        old = subprocess.run
        try:
            subprocess.run = lambda *a, **k: subprocess.CompletedProcess(a, 1, "", "gh: Not Found (HTTP 404)")
            self.assertFalse(bump.gh_status("o/r", EC)("v1.0.0").published)
            subprocess.run = lambda *a, **k: subprocess.CompletedProcess(a, 1, "", "HTTP 500")
            with self.assertRaises(RuntimeError):
                bump.gh_status("o/r", EC)("v1.0.0")
        finally:
            subprocess.run = old


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
        self.assertEqual((o["bump"], o["version"], o["prerelease"]), ("patch", "1.0.1", "false"))

    def test_no_tag_is_1_0_0(self):
        self.assertEqual(self.run_cli("next", "--labels", "semver:minor")["version"], "1.0.0")

    def test_labels_as_json_array_and_deploy_manual(self):
        o = self.run_cli("next", "--latest-tag", "v1.0.0", "--labels", '["deploy:manual","semver:minor"]')
        self.assertEqual((o["version"], o["prerelease"]), ("1.1.0", "true"))

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
            # a missing "before" file means the file is new: its entries are additions
            o = self.run_cli("next", "--latest-tag", "v1.4.2", "--paths-file", p,
                             "--features-before", os.path.join(d, "missing.json"), "--features-after", a)
            self.assertEqual(o["bump"], "minor")

    def test_bad_latest_tag_is_an_error(self):
        with self.assertRaises(SystemExit), redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            bump.main(["next", "--latest-tag", "1.0"])

    def test_release_needs_repo_or_offline(self):
        with self.assertRaises(SystemExit), redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            bump.main(["release"])

    def test_multiline_output_uses_heredoc(self):
        out = io.StringIO()
        bump.emit({"a": "x", "notes": "l1\nl2\n"}, out)
        lines = out.getvalue().splitlines()
        self.assertEqual(lines[0], "a=x")
        self.assertRegex(lines[1], r"^notes<<EOF_NOTES_[0-9a-f]{16}$")
        self.assertEqual(lines[2:], ["l1", "l2", lines[1].split("<<")[1]])


class Notes(unittest.TestCase):
    """release_notes with fake file reads: summary (eve-contracts, multi-box) and full (hangarscope)."""

    INDEX = ("| Date | Change | Summary |\n| --- | --- | --- |\n"
             "| 2026-10-18 | [Export](2026-10-18-export/README.md) | Export to CSV |\n")
    ENTRY = "# Export\n\n## What\n\nExports. ![after](after.png) [docs](../../README.md) [x](https://e.com)\n"

    def plan(self, previous="v1.0.0", changes=None):
        changes = changes if changes is not None else [
            bump.Change("a" * 40, "Export (#21)", PullRequest(21, "Export CSV"), Decision("minor", "x")),
            bump.Change("b" * 40, "tweak", None, Decision("patch", "y"))]
        return bump.Plan(True, True, "r", version="1.1.0", bump="minor", previous=previous,
                         head="c" * 40, changes=changes)

    def reader(self, cfg):
        files = {("v1.0.0", cfg.features_path): features(),
                 ("c" * 40, cfg.features_path): features(21),
                 ("c" * 40, cfg.changelog_prefix + "README.md"): self.INDEX,
                 ("c" * 40, cfg.changelog_prefix + "2026-10-18-export/README.md"): self.ENTRY}
        return lambda rev, path: files.get((rev, path))

    def test_summary_notes(self):
        n = bump.release_notes(self.plan(), EC, "o/r", self.reader(EC),
                               added=["changelog/2026-10-18-export/README.md", "changelog/2026-10-18-export/after.png"])
        self.assertIn("## What's new\n\n### Feature 21\n\nDoes 21.", n)
        self.assertIn("## Changes\n\n- [Export](https://github.com/o/r/blob/" + "c" * 40
                      + "/changelog/2026-10-18-export/README.md): Export to CSV", n)
        self.assertIn("## Pull requests\n\n- Export CSV (#21)\n- tweak (bbbbbbb)", n)
        self.assertIn("compare/v1.0.0...v1.1.0", n)
        self.assertFalse(n.startswith("# "))

    def test_entry_without_index_row_still_listed(self):
        n = bump.release_notes(self.plan(), EC, "o/r", lambda r, p: None, added=["changelog/2026-10-19-x/README.md"])
        self.assertIn("- [2026-10-19-x](https://github.com/o/r/blob/", n)

    def test_full_notes_inline_entries_with_demoted_headings_and_pinned_links(self):
        n = bump.release_notes(self.plan(), HS, "o/r", self.reader(HS),
                               added=["docs/changelog/2026-10-18-export/README.md"])
        self.assertTrue(n.startswith("# HangarScope v1.1.0\n"))
        self.assertIn("### Export\n\n#### What", n)
        sha = "c" * 40
        self.assertIn(f"![after](https://github.com/o/r/blob/{sha}/docs/changelog/2026-10-18-export/after.png?raw=true)", n)
        self.assertIn(f"[docs](https://github.com/o/r/blob/{sha}/docs/changelog/2026-10-18-export/../../README.md)", n)
        self.assertIn("[x](https://e.com)", n)

    def test_entries_oldest_first_and_only_top_level_readmes(self):
        e = bump.release_log_entries(["changelog/2026-10-19-b/README.md", "changelog/2026-10-18-a/README.md",
                                      "changelog/README.md", "changelog/x/y/README.md", "docs/README.md"], EC)
        self.assertEqual(e, ["2026-10-18-a", "2026-10-19-b"])

    def test_what_is_new_only_for_new_entries(self):
        cfg = EC
        n = bump.release_notes(self.plan(), cfg, "o/r", lambda r, p: features(21) if p == cfg.features_path else None,
                               added=[])
        self.assertNotIn("What's new", n)
        self.assertNotIn("## Changes", n)

    def test_first_release_has_no_compare_link(self):
        n = bump.release_notes(self.plan(previous=""), EC, "o/r", lambda r, p: None, added=[])
        self.assertNotIn("compare", n)

    def test_one_pr_listed_once(self):
        pr = PullRequest(5, "Five")
        changes = [bump.Change("a" * 40, "x", pr, Decision("patch", "")), bump.Change("b" * 40, "y", pr, Decision("patch", ""))]
        n = bump.release_notes(self.plan(changes=changes), EC, "o/r", lambda r, p: None, added=[])
        self.assertEqual(n.count("(#5)"), 1)


class GitRepo(unittest.TestCase):
    """Release and preview planning against a real throwaway repo; GitHub is faked."""

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
        sha = self.head()
        if pr:
            self.prs[sha] = PullRequest(pr, msg, list(labels))
        return sha

    def head(self):
        return bump.git("rev-parse", "HEAD").strip()

    def tag(self, name, rev="HEAD", annotated=False):
        extra = ["-a", "-m", name] if annotated else []
        subprocess.run(["git", "tag", *extra, name, rev], check=True)

    def lookup(self, sha):
        return self.prs.get(sha)

    def plan(self, head="HEAD", cfg=EC, status=EVERYTHING, force=""):
        return plan_release(head, cfg, self.lookup, status, force)

    # --- the normal path

    def test_no_tags_first_release_is_1_0_0(self):
        self.commit("feat", {"a.cs": "1"}, pr=1)
        p = self.plan()
        self.assertTrue(p.release and p.finalize)
        self.assertEqual(p.version, "1.0.0")
        self.assertIn("first release", p.reason)

    def test_one_pr_after_v1_0_0(self):
        self.tag("v1.0.0")
        self.commit("Fix thing", {"a.cs": "1"}, pr=20)
        p = self.plan()
        self.assertEqual((p.bump, p.version, p.tag, p.previous), ("patch", "1.0.1", "v1.0.1", "v1.0.0"))
        self.assertEqual(len(p.changes), 1)
        self.assertFalse(p.prerelease)

    def test_announcement_entry_is_minor_and_in_notes(self):
        self.commit("add features file", {F: features()})
        self.tag("v1.0.0")
        self.commit("Export CSV", {F: features(21), "changelog/2026-10-18-export/README.md": "x",
                                   "changelog/README.md": Notes.INDEX}, pr=21)
        p = self.plan()
        self.assertEqual((p.bump, p.version), ("minor", "1.1.0"))
        notes = bump.release_notes(p, EC, "o/r")
        self.assertIn("### Feature 21", notes)
        self.assertIn("Does 21.", notes)
        self.assertIn("- Export CSV (#21)", notes)
        self.assertIn("changelog/2026-10-18-export/README.md): Export to CSV", notes)
        self.assertIn("compare/v1.0.0...v1.1.0", notes)

    def test_editing_an_announcement_entry_is_patch(self):
        self.commit("features", {F: features(1)})
        self.tag("v1.0.0")
        self.commit("Typo", {F: features(1).replace("Does 1.", "Does one.")}, pr=2)
        self.assertEqual(self.plan().version, "1.0.1")

    def test_label_from_pr_lookup(self):
        self.tag("v1.0.0")
        self.commit("Breaking", {"a.cs": "1"}, pr=22, labels=["semver:major"])
        self.assertEqual(self.plan().version, "2.0.0")

    def test_deploy_manual_makes_a_prerelease(self):
        self.tag("v1.0.0")
        self.commit("Needs a scope", {"a.cs": "1"}, pr=23, labels=["deploy:manual"])
        p = self.plan()
        self.assertTrue(p.prerelease)
        self.assertEqual(p.version, "1.0.1")

    def test_several_merges_share_one_version_biggest_bump_and_any_hold(self):
        self.commit("features", {F: features()})
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=26, labels=["semver:patch"])
        self.commit("Feature", {F: features(27)}, pr=27)
        self.commit("Docs", {"d.md": "1"}, pr=28, labels=["deploy:manual"])
        p = self.plan()
        self.assertEqual((p.bump, p.version), ("minor", "1.1.0"))  # #26's semver:patch doesn't hide #27
        self.assertEqual([c.pr.number for c in p.changes], [26, 27, 28])
        self.assertIn("3 merges", p.reason)
        self.assertIn("#27 minor", p.reason)
        self.assertTrue(p.prerelease)

    def test_direct_push_without_pr_is_patch(self):
        self.tag("v1.0.0")
        sha = self.commit("tweak docs", {"d.md": "1"})
        p = self.plan()
        self.assertEqual(p.version, "1.0.1")
        self.assertIn("direct push " + sha[:7], p.reason)
        self.assertIn(f"- tweak docs ({sha[:7]})", bump.release_notes(p, EC, "o/r"))

    def test_force_bump_overrides(self):
        self.tag("v1.0.0")
        self.commit("Docs", {"d.md": "1"}, pr=30)
        p = self.plan(force="minor")
        self.assertEqual((p.version, p.reason), ("1.1.0", "manual override: minor"))

    def test_highest_tag_not_most_recent(self):
        self.tag("v1.10.0")
        self.tag("v1.9.0")
        self.commit("Fix", {"a.cs": "1"}, pr=31)
        self.assertEqual(self.plan().version, "1.10.1")

    def test_annotated_tags(self):
        self.tag("v1.0.0", annotated=True)
        self.commit("Fix", {"a.cs": "1"}, pr=40)
        p = self.plan()
        self.assertEqual((p.version, p.previous), ("1.0.1", "v1.0.0"))
        self.tag("v1.0.1", annotated=True)
        p = self.plan()
        self.assertTrue(p.reused_tag)
        self.assertEqual(p.version, "1.0.1")

    def test_merge_commits_count_once_with_their_own_diff(self):
        # hangarscope and multi-box merge with merge commits: only first-parent commits count
        self.commit("features", {F: features()})
        self.tag("v1.0.0")
        subprocess.run(["git", "switch", "-q", "-c", "feature"], check=True)
        self.commit("wip 1", {"a.cs": "1"})
        self.commit("wip 2", {F: features(50)})
        subprocess.run(["git", "switch", "-q", "main"], check=True)
        subprocess.run(["git", "merge", "-q", "--no-ff", "-m", "Merge pull request #50", "feature"], check=True)
        self.prs[self.head()] = PullRequest(50, "Feature", [])
        p = self.plan()
        self.assertEqual([c.pr.number for c in p.changes], [50])
        self.assertEqual(p.version, "1.1.0")

    # --- bookkeeping and legacy commits

    def test_bookkeeping_commit_is_ignored(self):
        self.tag("v1.0.0")
        self.commit("Release v1.0.0: changelog and version", {"CHANGELOG.md": "## [1.0.0]",
                                                              "src/App/App.csproj": "<Version>1.0.0</Version>"})
        p = self.plan(cfg=MB)
        self.assertFalse(p.release or p.finalize)
        self.assertIn("no unreleased changes since v1.0.0", p.reason)

    def test_bookkeeping_then_a_merge_releases_only_the_merge(self):
        self.tag("v1.0.0")
        self.commit("Release v1.0.0: changelog and version", {"CHANGELOG.md": "x"})
        self.commit("Fix", {"a.cs": "1"}, pr=9)
        p = self.plan(cfg=MB)
        self.assertEqual([c.pr.number for c in p.changes], [9])
        self.assertEqual(p.version, "1.0.1")

    def test_release_named_commit_with_code_is_a_change(self):
        self.tag("v1.0.0")
        self.commit("Release v1.0.1: changelog and version", {"CHANGELOG.md": "x", "src/a.cs": "1"})
        self.assertEqual(self.plan(cfg=MB).version, "1.0.1")

    def test_legacy_release_commit_is_ignored(self):
        self.tag("v1.0.0")
        self.commit("Release v1.0.1", {"x.csproj": "<Version>1.0.1</Version>"})
        self.tag("v1.0.1")
        self.commit("Feature", {"a.cs": "1"}, pr=29, labels=["semver:minor"])
        p = self.plan()
        self.assertEqual(p.version, "1.1.0")
        self.assertEqual(len(p.changes), 1)

    def test_only_legacy_commits_means_nothing_to_release(self):
        self.tag("v1.0.0")
        self.commit("Release v1.0.1", {"x.csproj": "1"})
        self.assertFalse(self.plan().release)

    # --- re-runs and tags at HEAD

    def test_tagged_head_with_published_release_only_refinalizes(self):
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=23, labels=["deploy:manual"])
        self.tag("v1.0.1")
        p = self.plan()
        self.assertTrue(p.reused_tag)
        self.assertFalse(p.release)
        self.assertTrue(p.finalize)
        self.assertTrue(p.prerelease)
        self.assertEqual((p.version, p.previous), ("1.0.1", "v1.0.0"))
        self.assertEqual([c.pr.number for c in p.changes], [23])
        self.assertIn("- Fix (#23)", bump.release_notes(p, EC, "o/r"))

    def test_tagged_head_without_release_is_built_again(self):
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=47)
        self.tag("v1.0.1")
        p = self.plan(status=published_except("v1.0.1"))
        self.assertTrue(p.reused_tag and p.release and p.finalize)
        self.assertEqual((p.version, p.previous), ("1.0.1", "v1.0.0"))

    def test_tagged_head_after_bookkeeping_commit(self):
        # finalize re-run on the tagged commit while the bookkeeping commit sits on top of it
        self.tag("v1.0.0")
        tagged = self.commit("Fix", {"a.cs": "1"}, pr=8)
        self.tag("v1.0.1")
        self.commit("Release v1.0.1: changelog and version", {"CHANGELOG.md": "x"})
        p = self.plan(head=tagged, cfg=MB)
        self.assertEqual((p.release, p.finalize, p.version), (False, True, "1.0.1"))
        self.assertFalse(self.plan(cfg=MB).finalize)  # and the tip has nothing new

    def test_commit_inside_a_newer_release_is_skipped(self):
        self.tag("v1.0.0")
        older = self.commit("A", {"a.cs": "1"}, pr=24)
        self.commit("B", {"b.cs": "1"}, pr=25)
        self.tag("v1.0.1")
        p = self.plan(head=older)
        self.assertFalse(p.release or p.finalize)
        self.assertIn("already part of v1.0.1", p.reason)

    def test_commit_inside_a_later_unpublished_tag_is_skipped_too(self):
        # an old run re-run while a newer run owns a tag on top of it: never publish older code
        self.tag("v1.0.0")
        older = self.commit("A", {"a.cs": "1"}, pr=24)
        self.commit("B", {"b.cs": "1"}, pr=25)
        self.tag("v1.0.1")
        self.assertFalse(self.plan(head=older, status=published_except("v1.0.1")).release)

    def test_rerun_of_superseded_tag_is_skipped(self):
        self.tag("v1.0.0")
        old = self.commit("Fix", {"a.cs": "1"}, pr=45)
        self.tag("v1.0.1")  # never published
        self.commit("Docs", {"d.md": "1"}, pr=46)
        self.tag("v1.0.2")  # the next run released both
        p = self.plan(head=old, status=published_except("v1.0.1"))
        self.assertFalse(p.finalize)
        self.assertIn("superseded by v1.0.2", p.reason)

    # --- only tags in HEAD's history are a base

    def test_tag_outside_heads_history_is_not_a_base_but_is_numbered_above(self):
        base = self.head()
        subprocess.run(["git", "switch", "-q", "-c", "side"], check=True)
        self.commit("side", {"s.cs": "1"})
        self.tag("v1.0.0")
        subprocess.run(["git", "switch", "-q", "main"], check=True)
        self.assertEqual(self.head(), base)
        self.commit("main work", {"m.cs": "1"}, pr=32)
        p = self.plan()
        self.assertEqual(p.previous, "")  # v1.0.0 isn't in main's history
        self.assertEqual(p.version, "1.0.1")  # but the new tag still goes above it
        self.assertEqual(len(p.changes), 2)

    def test_newer_tag_elsewhere_does_not_become_the_base(self):
        self.tag("v1.0.0")
        fork = self.head()
        self.commit("later", {"l.cs": "1"}, pr=60)
        self.tag("v1.1.0")
        subprocess.run(["git", "switch", "-q", "-c", "old", fork], check=True)
        self.commit("old branch work", {"o.cs": "1"}, pr=61)
        p = self.plan()
        self.assertEqual(p.previous, "v1.0.0")
        self.assertEqual([c.pr.number for c in p.changes], [61])
        self.assertEqual(p.version, "1.1.1")

    # --- unpublished (dangling) tags

    def test_unpublished_tag_is_rolled_forward(self):
        # run for #41 tagged v1.1.0 then failed before publishing; #42's run must
        # release both, above the dangling tag, and list #41 in the notes
        self.commit("features", {F: features()})
        self.tag("v1.0.0")
        self.commit("Feature", {F: features(41)}, pr=41)
        self.tag("v1.1.0")
        self.commit("Fix", {"a.cs": "1"}, pr=42)
        p = self.plan(status=published_except("v1.1.0"))
        self.assertEqual((p.previous, p.bump, p.version), ("v1.0.0", "minor", "1.1.1"))
        self.assertEqual([c.pr.number for c in p.changes], [41, 42])
        self.assertIn("numbered above unpublished v1.1.0", p.reason)
        self.assertIn("### Feature 41", bump.release_notes(p, EC, "o/r"))

    def test_unpublished_tag_rolled_forward_keeps_bigger_bump(self):
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=43)
        self.tag("v1.0.1")
        self.commit("Feature", {"b.cs": "1"}, pr=44, labels=["semver:minor"])
        p = self.plan(status=published_except("v1.0.1"))
        self.assertEqual(p.version, "1.1.0")  # above v1.0.1 already, no collision

    def test_draft_release_is_not_published(self):
        self.tag("v1.0.0")
        self.commit("Fix", {"a.cs": "1"}, pr=48)
        draft = ReleaseStatus(True, True, False, True, True)
        p = self.plan(status=lambda t: draft)
        self.assertEqual((p.previous, p.version), ("", "1.0.1"))
        self.assertEqual(len(p.changes), 2)  # whole history: nothing was ever published

    # --- PR previews

    def preview(self, base, labels=(), status=EVERYTHING, cfg=EC):
        return plan_preview(base, "HEAD", list(labels), cfg, self.lookup, status)

    def test_preview_against_base(self):
        self.commit("features", {F: features()})
        self.tag("v1.2.0")
        base = self.head()
        self.commit("Feature", {F: features(33)})
        p = self.preview(base)
        self.assertEqual((p.bump, p.version, p.previous), ("minor", "1.3.0", "v1.2.0"))

    def test_preview_labels_and_hold(self):
        self.tag("v1.0.0")
        base = self.head()
        self.commit("change", {"b.cs": "b"})
        p = self.preview(base, ["semver:major", "deploy:manual"])
        self.assertEqual((p.version, p.prerelease), ("2.0.0", True))

    def test_preview_from_a_branch_behind_the_base(self):
        # PR branch forked before main moved: only the PR's own diff decides
        self.commit("features", {F: features()})
        self.tag("v1.0.0")
        fork = self.head()
        self.commit("main feature", {F: features(70)}, pr=70)
        self.tag("v1.1.0")
        main = self.head()
        subprocess.run(["git", "switch", "-q", "-c", "pr", fork], check=True)
        self.commit("pr fix", {"p.cs": "1"})
        p = self.preview(main)
        self.assertEqual((p.bump, p.version), ("patch", "1.1.1"))

    def test_preview_includes_unreleased_merges_on_the_base(self):
        self.commit("features", {F: features()})
        self.tag("v1.0.0")
        self.commit("Feature", {F: features(5)}, pr=5)
        self.commit("Docs", {"d.md": "x"}, pr=6, labels=["deploy:manual"])
        base = self.head()
        self.commit("PR change", {"b.cs": "b"})
        p = self.preview(base)
        self.assertEqual((p.version, p.prerelease), ("1.1.0", True))
        self.assertIn("2 unreleased merge(s)", p.reason)

    def test_preview_matches_release_with_unpublished_tag(self):
        self.commit("features", {F: features()})
        self.tag("v1.0.0")
        self.commit("Feature A", {F: features(50)}, pr=50)
        self.tag("v1.1.0")  # its run failed before publishing
        base = self.head()
        self.commit("Feature B", {F: features(51, 50)}, pr=51)
        status = published_except("v1.1.0")
        rel = self.plan(status=status)
        pre = self.preview(base, status=status)
        self.assertEqual((rel.version, pre.version, pre.previous), ("1.1.1", "1.1.1", "v1.0.0"))

    def test_preview_fails_closed_on_lookup_errors(self):
        self.tag("v1.0.0")
        self.commit("unreleased", {"u.cs": "1"}, pr=80)
        base = self.head()
        self.commit("PR", {"p.cs": "1"})

        def broken(sha):
            raise RuntimeError("could not look up")
        with self.assertRaises(RuntimeError):
            plan_preview(base, "HEAD", [], EC, broken, EVERYTHING)

    def test_cli_release_offline_and_pr(self):
        self.commit("features", {F: features()})
        self.tag("v1.2.0")
        base = self.head()
        self.commit("Feature", {F: features(33)})
        with tempfile.TemporaryDirectory() as d:
            gout, summ = os.path.join(d, "out"), os.path.join(d, "summary")
            out = io.StringIO()
            with redirect_stdout(out):
                bump.main(["pr", "--offline", "--base", base, "--labels", '["deploy:manual"]',
                           "--github-output", gout, "--summary", summ])
            o = dict(l.split("=", 1) for l in out.getvalue().splitlines())
            self.assertEqual((o["version"], o["prerelease"]), ("1.3.0", "true"))
            self.assertIn("Merging this PR would release **v1.3.0**", o["preview"])
            with open(summ) as f:
                self.assertIn("### Merging this PR would release v1.3.0", f.read())
            with open(gout) as f:
                self.assertIn("version=1.3.0", f.read())
            out = io.StringIO()
            with redirect_stdout(out):
                bump.main(["release", "--offline"])
            text = out.getvalue()
            self.assertIn("release=true", text)
            self.assertIn("version=1.3.0", text)
            self.assertRegex(text, r"notes<<EOF_NOTES_")


if __name__ == "__main__":
    unittest.main()
