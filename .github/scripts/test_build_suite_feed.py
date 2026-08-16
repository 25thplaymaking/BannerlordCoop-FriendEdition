#!/usr/bin/env python3

from __future__ import annotations

import unittest

from build_suite_feed import build_manifest, is_newer


class SuiteFeedTests(unittest.TestCase):
    def test_builds_content_addressed_r2_feed(self) -> None:
        digest = "A" * 64

        manifest = build_manifest("2026.08.16.1", digest, "9985798963", "Fourteen-module suite")

        self.assertEqual("2026.08.16.1", manifest["version"])
        self.assertEqual("a" * 64, manifest["sha256"])
        self.assertEqual(
            "https://pub-bf6bfe4b880e4d1b83f4b09b10419f78.r2.dev/"
            f"suite/objects/{'a' * 64}/Coop-suite.zip",
            manifest["clientZipUrl"],
        )
        self.assertEqual("Fourteen-module suite", manifest["notes"])

    def test_rejects_values_the_launcher_cannot_safely_promote(self) -> None:
        valid_hash = "a" * 64
        invalid_cases = (
            ("release-1", valid_hash, "1", "notes"),
            ("1.0", "short", "1", "notes"),
            ("1.0", valid_hash, "0", "notes"),
            ("1.0", valid_hash, "not-a-number", "notes"),
            ("1.0", valid_hash, "1", "two\nlines"),
        )

        for values in invalid_cases:
            with self.subTest(values=values), self.assertRaises(ValueError):
                build_manifest(*values)

    def test_version_order_matches_launcher(self) -> None:
        self.assertTrue(is_newer("2026.8.16.1", "2026.8.15.1943"))
        self.assertTrue(is_newer("1.0.1", "1"))
        self.assertFalse(is_newer("1.0", "1"))
        self.assertFalse(is_newer("1.9", "2.0"))
        self.assertFalse(is_newer("release-2", "1.0"))


if __name__ == "__main__":
    unittest.main()
