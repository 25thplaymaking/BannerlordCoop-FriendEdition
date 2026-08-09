#!/usr/bin/env python3
"""Read-only dedicated-server Harmony package and activation preflight."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


def fail(message: str) -> None:
    raise RuntimeError(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def normalize_module_ids(values: list[str]) -> list[str]:
    result: list[str] = []
    for value in values:
        for part in re.split(r"[,;*]", value):
            item = part.strip()
            if item and item not in {"_MODULES_", "MODULES"}:
                result.append(item)
    return result


def strip_json_comments(text: str) -> str:
    result: list[str] = []
    index = 0
    in_string = escaped = line_comment = block_comment = False
    while index < len(text):
        current = text[index]
        following = text[index + 1] if index + 1 < len(text) else ""
        if line_comment:
            if current in "\r\n":
                line_comment = False
                result.append(current)
            index += 1
            continue
        if block_comment:
            if current == "*" and following == "/":
                block_comment = False
                index += 2
                continue
            if current in "\r\n":
                result.append(current)
            index += 1
            continue
        if in_string:
            result.append(current)
            if escaped:
                escaped = False
            elif current == "\\":
                escaped = True
            elif current == '"':
                in_string = False
            index += 1
            continue
        if current == '"':
            in_string = True
            result.append(current)
            index += 1
        elif current == "/" and following == "/":
            line_comment = True
            index += 2
        elif current == "/" and following == "*":
            block_comment = True
            index += 2
        else:
            result.append(current)
            index += 1
    if in_string or block_comment:
        fail("Runtime mod-config.json contains an unterminated string or block comment.")
    return "".join(result)


def remove_trailing_commas(text: str) -> str:
    result: list[str] = []
    in_string = escaped = False
    for index, current in enumerate(text):
        if in_string:
            result.append(current)
            if escaped:
                escaped = False
            elif current == "\\":
                escaped = True
            elif current == '"':
                in_string = False
            continue
        if current == '"':
            in_string = True
            result.append(current)
            continue
        if current == ",":
            look_ahead = index + 1
            while look_ahead < len(text) and text[look_ahead].isspace():
                look_ahead += 1
            if look_ahead < len(text) and text[look_ahead] in "}]":
                continue
        result.append(current)
    return "".join(result)


def read_runtime_config(path: Path) -> dict:
    parsed = json.loads(remove_trailing_commas(strip_json_comments(path.read_text(encoding="utf-8"))))
    if not isinstance(parsed, dict):
        fail("Resolved runtime mod-config root must be an object.")
    return parsed


def harmony_files(modules_root: Path) -> list[Path]:
    found: list[Path] = []
    visited: set[Path] = set()
    for current, directories, files in os.walk(modules_root, followlinks=True):
        current_path = Path(current)
        resolved = current_path.resolve()
        if resolved in visited:
            directories[:] = []
            continue
        visited.add(resolved)
        if "0Harmony.dll" in files:
            found.append(current_path / "0Harmony.dll")
    return found


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate pinned Friend Edition server Harmony bytes and launch order without modifying or launching the server."
    )
    parser.add_argument("--server-root", required=True, help="Dedicated-server engine root containing Modules")
    parser.add_argument(
        "--runtime-mod-config",
        help="Resolved runtime CoopData mod-config.json; defaults through COOP_DATA_DIR then BANNERLORD_USER_DIR",
    )
    parser.add_argument(
        "--active-modules",
        required=True,
        nargs="+",
        help="Launch module IDs in order; pass as separate values or one comma/*-separated string",
    )
    args = parser.parse_args()

    script_root = Path(__file__).resolve().parent
    expectation_path = script_root / "SERVER-HARMONY.json"
    if not expectation_path.is_file():
        fail(f"Pinned server Harmony expectation is missing: {expectation_path}")
    expectation = json.loads(expectation_path.read_text(encoding="utf-8"))

    runtime_config_input = args.runtime_mod_config
    if not runtime_config_input:
        for environment_name in expectation["runtimeConfig"]["environmentDirectoryPrecedence"]:
            directory = os.environ.get(environment_name)
            if directory:
                runtime_config_input = str(Path(directory) / expectation["runtimeConfig"]["fileName"])
                break
    if not runtime_config_input:
        fail("Pass --runtime-mod-config or set COOP_DATA_DIR/BANNERLORD_USER_DIR.")
    runtime_config_path = Path(runtime_config_input).expanduser().resolve()
    if runtime_config_path.is_dir():
        runtime_config_path /= expectation["runtimeConfig"]["fileName"]
    if not runtime_config_path.is_file():
        fail(f"Resolved runtime CoopData mod-config.json is missing: {runtime_config_path}")
    runtime_config_hash = sha256(runtime_config_path)
    runtime_config = read_runtime_config(runtime_config_path)
    difficulty = runtime_config.get("difficulty")
    if not isinstance(difficulty, dict) or difficulty.get("birthAndDeath") is not True:
        fail(
            "Resolved runtime config must set difficulty.birthAndDeath=true as a Boolean: "
            f"{runtime_config_path}"
        )

    server_root = Path(args.server_root).expanduser().resolve()
    modules_root = server_root / "Modules"
    if not server_root.is_dir() or not modules_root.is_dir():
        fail(f"Dedicated-server engine root does not contain Modules: {server_root}")

    module_ids = normalize_module_ids(args.active_modules)
    if not module_ids or len(set(module_ids)) != len(module_ids):
        fail("The launch module list is empty or contains duplicate IDs.")
    activation = expectation["activation"]
    for required in activation["requiredModuleIds"]:
        if required not in module_ids:
            fail(f"Required server module '{required}' is absent from the launch module list.")
    blocked_server_modules = set(activation.get("neverActivateModuleIds", []))
    blocked_server_modules.update(activation.get("guardedModuleIds", []))
    for blocked in sorted(blocked_server_modules):
        if blocked in module_ids:
            fail(
                f"Production server preflight blocks staged-inactive module '{blocked}'; "
                "remove it from the headless launch module list."
            )
    expected_order = activation["exactActiveModuleOrder"]
    if module_ids != expected_order:
        fail(
            "Production server launch modules must exactly match the pinned order: "
            + " -> ".join(expected_order)
            + ". Received: "
            + " -> ".join(module_ids)
            + "."
        )
    harmony_id = expectation["moduleId"]
    for successor in activation["harmonyMustPrecede"]:
        if module_ids.index(harmony_id) >= module_ids.index(successor):
            fail(f"Server activation order must place '{harmony_id}' before '{successor}'.")

    module_root = modules_root / harmony_id
    descriptor_path = module_root / "SubModule.xml"
    if not descriptor_path.is_file():
        fail(f"Pinned Harmony module descriptor is missing: {descriptor_path}")
    descriptor = ET.parse(descriptor_path).getroot()
    module_id_node = descriptor.find("Id")
    version_node = descriptor.find("Version")
    actual_id = module_id_node.attrib.get("value") if module_id_node is not None else None
    actual_version = version_node.attrib.get("value") if version_node is not None else None
    if actual_id != harmony_id or actual_version != expectation["moduleVersion"]:
        fail(f"Harmony descriptor is not pinned {harmony_id} {expectation['moduleVersion']}.")

    if not expectation["metadataAudit"]["buildTimeVerified"]:
        fail("The package does not attest build-time AssemblyName/version verification for Harmony.")
    module_real = module_root.resolve()
    for payload in expectation["payloads"]:
        path = (module_root / Path(payload["path"])).resolve()
        if module_real not in path.parents or not path.is_file():
            fail(f"Pinned server Harmony payload is missing or escapes its module root: {path}")
        if path.stat().st_size != int(payload["size"]) or sha256(path) != payload["sha256"]:
            fail(f"Server Harmony payload failed its pinned size/SHA-256 check: {path}")
        if not payload.get("assemblyFullName"):
            fail(f"Pinned build-time AssemblyName/version record is missing for: {payload['path']}")

    unexpected = []
    for candidate in harmony_files(modules_root):
        resolved = candidate.resolve()
        if module_real not in resolved.parents:
            unexpected.append(str(candidate))
    if unexpected:
        fail("More than one packaged Harmony provider is present: " + "; ".join(sorted(unexpected)))

    for payload in expectation["payloads"]:
        path = (module_root / Path(payload["path"])).resolve()
        if sha256(path) != payload["sha256"]:
            fail(f"Server Harmony payload changed during read-only preflight: {path}")
    if sha256(runtime_config_path) != runtime_config_hash:
        fail(f"Resolved runtime mod-config changed during read-only preflight: {runtime_config_path}")

    print(
        f"PASS: exact build-time-audited AssemblyName/version byte pins for {harmony_id} "
        f"{expectation['moduleVersion']} are the only packaged Harmony provider and precede Native/Coop."
    )
    print(
        f"CONFIG: resolvedRuntimePath={runtime_config_path} sha256={runtime_config_hash} "
        "difficulty.birthAndDeath=true"
    )
    print("This preflight validates only; it does not install, delete, overwrite, bootstrap, or launch the server.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, ET.ParseError, RuntimeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
