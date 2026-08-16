#!/usr/bin/env python3
"""Validate a multipart suite descriptor against GitHub release-asset metadata."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re

from prepare_github_suite_release import GITHUB_ASSET_LIMIT, PAYLOAD_TAG


def validate_descriptor(descriptor_path: Path, assets_path: Path, repository: str) -> dict[str, object]:
    manifest = json.loads(descriptor_path.read_text(encoding="utf-8"))
    assets_document = json.loads(assets_path.read_text(encoding="utf-8"))
    assets = assets_document.get("assets", assets_document)
    if not isinstance(assets, list):
        raise ValueError("GitHub asset metadata must be a list")

    version = manifest.get("version")
    complete_sha = str(manifest.get("sha256", "")).lower()
    notes = manifest.get("notes")
    parts = manifest.get("parts")
    if not isinstance(version, str) or not re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", version):
        raise ValueError("descriptor version must be dotted numeric")
    if not re.fullmatch(r"[0-9a-f]{64}", complete_sha):
        raise ValueError("descriptor SHA-256 is invalid")
    if not isinstance(notes, str) or not notes.strip() or len(notes) > 500 or "\n" in notes or "\r" in notes:
        raise ValueError("descriptor notes must be one non-empty line")
    if manifest.get("clientZipUrl") not in (None, ""):
        raise ValueError("multipart descriptor cannot also declare clientZipUrl")
    if not isinstance(parts, list) or not 0 < len(parts) <= 1000:
        raise ValueError("descriptor must contain 1 to 1000 parts")
    if descriptor_path.name != f"suite-{complete_sha}-v{version}.json":
        raise ValueError("descriptor filename must contain its complete SHA-256 and version")

    by_name = {asset.get("name"): asset for asset in assets if isinstance(asset, dict)}
    normalized_parts: list[dict[str, object]] = []
    seen_urls: set[str] = set()
    for index, part in enumerate(parts, start=1):
        if not isinstance(part, dict):
            raise ValueError(f"part {index} is not an object")
        name = f"Coop-suite.{complete_sha}.part{index:03d}"
        url = f"https://github.com/{repository}/releases/download/{PAYLOAD_TAG}/{name}"
        part_sha = str(part.get("sha256", "")).lower()
        part_bytes = part.get("bytes")
        if part.get("url") != url or url in seen_urls:
            raise ValueError(f"part {index} URL is not the expected immutable GitHub asset")
        if not isinstance(part_bytes, int) or not 0 < part_bytes < GITHUB_ASSET_LIMIT:
            raise ValueError(f"part {index} byte length is invalid")
        if not re.fullmatch(r"[0-9a-f]{64}", part_sha):
            raise ValueError(f"part {index} SHA-256 is invalid")
        asset = by_name.get(name)
        if asset is None:
            raise ValueError(f"GitHub release is missing {name}")
        if asset.get("size") != part_bytes:
            raise ValueError(f"GitHub byte length differs for {name}")
        if asset.get("digest") != f"sha256:{part_sha}":
            raise ValueError(f"GitHub digest differs for {name}")
        seen_urls.add(url)
        normalized_parts.append({"url": url, "bytes": part_bytes, "sha256": part_sha})

    return {
        "version": version,
        "sha256": complete_sha,
        "notes": notes.strip(),
        "parts": normalized_parts,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--descriptor", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    manifest = validate_descriptor(args.descriptor, args.assets, args.repository)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
