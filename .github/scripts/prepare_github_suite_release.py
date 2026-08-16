#!/usr/bin/env python3
"""Split an existing suite ZIP into immutable GitHub release assets and a feed descriptor."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re


DEFAULT_PART_BYTES = 1900 * 1024 * 1024
GITHUB_ASSET_LIMIT = 2 * 1024 * 1024 * 1024
PAYLOAD_TAG = "suite-payloads"
BUFFER_BYTES = 8 * 1024 * 1024


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(BUFFER_BYTES):
            digest.update(block)
    return digest.hexdigest()


def prepare_release(
    archive: Path,
    version: str,
    notes: str,
    repository: str,
    output_dir: Path,
    part_bytes: int = DEFAULT_PART_BYTES,
) -> tuple[Path, list[Path]]:
    archive = archive.resolve()
    output_dir = output_dir.resolve()
    version = version.strip()
    notes = notes.strip()
    repository = repository.strip()

    if not archive.is_file() or archive.stat().st_size <= 0:
        raise ValueError("archive must be a non-empty file")
    if archive.suffix.lower() != ".zip":
        raise ValueError("archive must be the launcher's ZIP payload")
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", version):
        raise ValueError("version must be dotted numeric")
    if not notes or len(notes) > 500 or "\n" in notes or "\r" in notes:
        raise ValueError("notes must be one non-empty line of at most 500 characters")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("repository must use owner/name")
    if part_bytes <= 0 or part_bytes >= GITHUB_ASSET_LIMIT:
        raise ValueError("part bytes must be positive and strictly below 2 GiB")
    if output_dir.exists():
        raise ValueError("output directory already exists; use a fresh directory")

    complete_sha = file_sha256(archive)
    output_dir.mkdir(parents=True)
    parts: list[dict[str, object]] = []
    part_paths: list[Path] = []
    split_digest = hashlib.sha256()

    with archive.open("rb") as source:
        index = 1
        while True:
            first = source.read(min(BUFFER_BYTES, part_bytes))
            if not first:
                break
            name = f"Coop-suite.{complete_sha}.part{index:03d}"
            path = output_dir / name
            digest = hashlib.sha256()
            written = 0
            with path.open("xb") as target:
                block = first
                while block:
                    target.write(block)
                    digest.update(block)
                    split_digest.update(block)
                    written += len(block)
                    remaining = part_bytes - written
                    if remaining == 0:
                        break
                    block = source.read(min(BUFFER_BYTES, remaining))

            parts.append(
                {
                    "url": f"https://github.com/{repository}/releases/download/{PAYLOAD_TAG}/{name}",
                    "bytes": written,
                    "sha256": digest.hexdigest(),
                }
            )
            part_paths.append(path)
            index += 1

    if split_digest.hexdigest() != complete_sha:
        raise RuntimeError("archive changed while it was being split")

    descriptor = output_dir / f"suite-{complete_sha}-v{version}.json"
    descriptor.write_text(
        json.dumps(
            {
                "version": version,
                "sha256": complete_sha,
                "notes": notes,
                "parts": parts,
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    upload_paths = [*part_paths, descriptor]
    (output_dir / "upload-files.txt").write_text(
        "\n".join(str(path) for path in upload_paths) + "\n",
        encoding="utf-8",
    )
    return descriptor, part_paths


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--notes", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--part-bytes", type=int, default=DEFAULT_PART_BYTES)
    args = parser.parse_args()

    descriptor, parts = prepare_release(
        args.archive,
        args.version,
        args.notes,
        args.repository,
        args.output_dir,
        args.part_bytes,
    )
    print(f"Prepared {len(parts)} parts")
    print(f"Descriptor: {descriptor.name}")
    print(f"Upload list: {descriptor.parent / 'upload-files.txt'}")


if __name__ == "__main__":
    main()
