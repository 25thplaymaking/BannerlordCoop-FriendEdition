#!/usr/bin/env python3
"""Build the launcher's suite feed for an already-uploaded public R2 object."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re


PUBLIC_BASE_URL = "https://pub-bf6bfe4b880e4d1b83f4b09b10419f78.r2.dev"
OBJECT_PREFIX = "suite/objects"


def is_newer(candidate: str, current: str) -> bool:
    """Match the launcher's dotted-numeric ordering, including trailing zero segments."""
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", candidate):
        return False
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", current):
        return False
    candidate_parts = [int(part) for part in candidate.split(".")]
    current_parts = [int(part) for part in current.split(".")]
    width = max(len(candidate_parts), len(current_parts))
    candidate_parts.extend([0] * (width - len(candidate_parts)))
    current_parts.extend([0] * (width - len(current_parts)))
    return candidate_parts > current_parts


def build_manifest(version: str, sha256: str, byte_count: str, notes: str) -> dict[str, object]:
    version = version.strip()
    sha256 = sha256.strip().lower()
    notes = notes.strip()

    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", version):
        raise ValueError("version must be dotted numeric")
    if not re.fullmatch(r"[0-9a-f]{64}", sha256):
        raise ValueError("sha256 must contain exactly 64 hexadecimal digits")
    try:
        payload_bytes = int(byte_count)
    except ValueError as error:
        raise ValueError("bytes must be a positive integer") from error
    if payload_bytes <= 0:
        raise ValueError("bytes must be a positive integer")
    if not notes or len(notes) > 500 or "\n" in notes or "\r" in notes:
        raise ValueError("notes must be one non-empty line of at most 500 characters")

    object_url = f"{PUBLIC_BASE_URL}/{OBJECT_PREFIX}/{sha256}/Coop-suite.zip"
    return {
        "version": version,
        "clientZipUrl": object_url,
        "sha256": sha256,
        "notes": notes,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--bytes", required=True)
    parser.add_argument("--notes", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    manifest = build_manifest(args.version, args.sha256, args.bytes, args.notes)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
