"""Tests for validate_features.py. Run: python3 -m unittest discover -s scripts/versioning -v"""

import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from validate_features import validate  # noqa: E402


def entry(pr=1, **kw):
    e = {"pr": pr, "date": "2026-10-18", "title": "Export to CSV",
         "description": "All Contracts has an Export button.\n\nIt uses your filters."}
    e.update(kw)
    return e


def doc(*entries):
    return json.dumps({"features": list(entries)})


class Validate(unittest.TestCase):
    def assertValid(self, text, **kw):
        self.assertEqual(validate(text, **kw), [])

    def assertError(self, text, fragment, **kw):
        errors = validate(text, **kw)
        self.assertTrue(any(fragment in e for e in errors), f"{fragment!r} not in {errors}")

    def test_empty_list_is_valid(self):
        self.assertValid('{"features": []}')

    def test_good_entries(self):
        self.assertValid(doc(entry(2), entry(1)))

    def test_invalid_json(self):
        self.assertError('{"features": [', "not valid JSON")

    def test_wrong_top_level(self):
        self.assertError("[]", "top level")
        self.assertError('{"feature": []}', "top level")

    def test_duplicate_pr(self):
        self.assertError(doc(entry(5), entry(5)), "already used by features[0]")

    def test_pr_must_be_positive_int(self):
        for bad in ("5", 5.0, True, 0, -1, None):
            self.assertError(doc(entry(bad)), '"pr" must be a positive integer')

    def test_missing_required_fields(self):
        for k in ("date", "title", "description"):
            e = entry()
            del e[k]
            self.assertError(doc(e), f'"{k}"')

    def test_blank_strings(self):
        self.assertError(doc(entry(title="  ")), '"title"')
        self.assertError(doc(entry(description="")), '"description"')

    def test_date_format_and_reality(self):
        self.assertError(doc(entry(date="18/10/2026")), "YYYY-MM-DD")
        self.assertError(doc(entry(date="2026-02-30")), "not a real date")

    def test_html_in_description(self):
        self.assertError(doc(entry(description="Click <b>Export</b>")), "plain text")
        self.assertValid(doc(entry(description="Profit < 5% and ISK > 0 still fine")))

    def test_unknown_field(self):
        self.assertError(doc(entry(extra=1)), "unknown field 'extra'")

    def test_entry_must_be_object(self):
        self.assertError(doc("x"), "must be an object")

    def test_images(self):
        good = [{"src": "whats-new/img/pr-1-x.png", "caption": "Header"}]
        self.assertValid(doc(entry(images=good)))
        self.assertError(doc(entry(images=[])), "1-2 images")
        self.assertError(doc(entry(images=good * 3)), "1-2 images")
        self.assertError(doc(entry(images=[{"src": "whats-new/img/a.png"}])), "exactly")
        self.assertError(doc(entry(images=[{"src": "docs/img/a.png", "caption": "c"}])), "under whats-new/img/")
        self.assertError(doc(entry(images=[{"src": "whats-new/img/../../x", "caption": "c"}])), "under whats-new/img/")
        self.assertError(doc(entry(images=[{"src": "whats-new/img/a.png", "caption": ""}])), '"caption"')

    def test_image_must_exist_when_repo_root_given(self):
        with tempfile.TemporaryDirectory() as root:
            os.makedirs(os.path.join(root, "whats-new/img"))
            open(os.path.join(root, "whats-new/img/ok.png"), "wb").close()
            imgs = lambda name: [{"src": f"whats-new/img/{name}", "caption": "c"}]
            self.assertValid(doc(entry(images=imgs("ok.png"))), repo_root=root)
            self.assertError(doc(entry(images=imgs("missing.png"))), "does not exist", repo_root=root)

    def test_added_entries_must_use_this_pr_number(self):
        base = doc(entry(10))
        self.assertValid(doc(entry(11), entry(10)), base_text=base, pr=11)
        self.assertError(doc(entry(12), entry(10)), 'use "pr": 11', base_text=base, pr=11)
        # an untouched old entry is not this PR's problem
        self.assertValid(doc(entry(10)), base_text=base, pr=11)
        # base without the file at all
        self.assertValid(doc(entry(11)), base_text="", pr=11)


if __name__ == "__main__":
    unittest.main()
