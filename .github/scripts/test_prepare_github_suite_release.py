#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from prepare_github_suite_release import GITHUB_ASSET_LIMIT, prepare_release
from validate_suite_descriptor import validate_descriptor


class GitHubSuiteReleaseTests(unittest.TestCase):
    def test_split_descriptor_reconstructs_and_validates_against_github_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as root_value:
            root = Path(root_value)
            archive = root / "Coop-suite.zip"
            original = bytes(range(64))
            archive.write_bytes(original)
            output = root / "payload"

            descriptor, part_paths = prepare_release(
                archive,
                "2026.08.16.2",
                "Fourteen-module suite",
                "owner/repository",
                output,
                part_bytes=17,
            )

            self.assertEqual(original, b"".join(path.read_bytes() for path in part_paths))
            manifest = json.loads(descriptor.read_text(encoding="utf-8"))
            self.assertEqual(hashlib.sha256(original).hexdigest(), manifest["sha256"])
            self.assertEqual(4, len(manifest["parts"]))
            upload_files = (output / "upload-files.txt").read_text(encoding="utf-8").splitlines()
            self.assertEqual([str(path) for path in [*part_paths, descriptor]], upload_files)

            assets = {
                "assets": [
                    {
                        "name": path.name,
                        "size": path.stat().st_size,
                        "digest": f"sha256:{hashlib.sha256(path.read_bytes()).hexdigest()}",
                    }
                    for path in part_paths
                ]
            }
            assets_path = root / "assets.json"
            assets_path.write_text(json.dumps(assets), encoding="utf-8")
            normalized = validate_descriptor(descriptor, assets_path, "owner/repository")

            self.assertEqual(manifest, normalized)

    def test_rejects_oversized_parts_and_existing_output(self) -> None:
        with tempfile.TemporaryDirectory() as root_value:
            root = Path(root_value)
            archive = root / "Coop-suite.zip"
            archive.write_bytes(b"payload")
            with self.assertRaises(ValueError):
                prepare_release(archive, "1.0", "notes", "owner/repo", root / "out", GITHUB_ASSET_LIMIT)

            output = root / "existing"
            output.mkdir()
            with self.assertRaises(ValueError):
                prepare_release(archive, "1.0", "notes", "owner/repo", output, 4)

    def test_validator_rejects_remote_digest_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as root_value:
            root = Path(root_value)
            archive = root / "Coop-suite.zip"
            archive.write_bytes(b"payload")
            descriptor, parts = prepare_release(
                archive, "1.0", "notes", "owner/repo", root / "payload", part_bytes=4
            )
            assets_path = root / "assets.json"
            assets_path.write_text(
                json.dumps(
                    {
                        "assets": [
                            {"name": part.name, "size": part.stat().st_size, "digest": "sha256:" + "0" * 64}
                            for part in parts
                        ]
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "digest differs"):
                validate_descriptor(descriptor, assets_path, "owner/repo")


if __name__ == "__main__":
    unittest.main()
