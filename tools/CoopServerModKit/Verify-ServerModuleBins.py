#!/usr/bin/env python3
"""Fail closed unless mirrored Workshop client runtimes exist in dedicated-server bins."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from xml.etree import ElementTree


DEFAULT_MODULES = (
    "PlayerSettlement",
    "ImprovedGarrisons",
    "DismembermentPlus",
    "Fourberie",
    "Bannerlord.Diplomacy",
    "UnblockableThrust",
)

DEFAULT_CLIENT_ONLY_MODULES = (
    "Bannerlord.UIExtenderEx",
    "Bannerlord.MBOptionScreen",
)


def runtime_files(root: Path) -> dict[str, Path]:
    if not root.is_dir():
        return {}
    return {
        path.relative_to(root).as_posix(): path
        for path in root.rglob("*")
        if path.is_file() and path.suffix.casefold() != ".pdb"
    }


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def declared_dlls(descriptor: Path) -> list[str]:
    try:
        root = ElementTree.parse(descriptor).getroot()
    except (OSError, ElementTree.ParseError) as error:
        raise ValueError(f"could not read {descriptor}: {error}") from error

    result: list[str] = []
    for element in root.findall("./SubModules/SubModule/DLLName"):
        value = element.attrib.get("value", "").strip()
        if value:
            result.append(value)
    return result


def verify_module(modules_root: Path, module_id: str) -> list[str]:
    module_root = modules_root / module_id
    client_root = module_root / "bin" / "Win64_Shipping_Client"
    server_root = module_root / "bin" / "Win64_Shipping_Server"
    client = runtime_files(client_root)
    server = runtime_files(server_root)
    errors: list[str] = []

    if not client:
        errors.append(f"{module_id}: client runtime is missing or empty at {client_root}")
        return errors
    if not server:
        errors.append(f"{module_id}: server runtime is missing or empty at {server_root}")
        return errors

    missing = sorted(set(client) - set(server))
    extra = sorted(set(server) - set(client))
    if missing:
        errors.append(f"{module_id}: server runtime is missing {', '.join(missing)}")
    if extra:
        errors.append(f"{module_id}: server runtime contains stale files {', '.join(extra)}")

    for relative in sorted(set(client) & set(server)):
        if sha256(client[relative]) != sha256(server[relative]):
            errors.append(f"{module_id}: server/client runtime bytes differ for {relative}")

    try:
        required = declared_dlls(module_root / "SubModule.xml")
    except ValueError as error:
        errors.append(f"{module_id}: {error}")
        required = []
    if not required:
        errors.append(f"{module_id}: SubModule.xml declares no loadable DLL")
    for dll_name in required:
        if dll_name not in server:
            errors.append(f"{module_id}: advertised submodule DLL is absent from server bin: {dll_name}")

    if not errors:
        print(f"SERVER MODULE BIN VERIFIED: {module_id} ({len(client)} files)")
    return errors


def verify_client_only_module(modules_root: Path, module_id: str) -> list[str]:
    module_root = modules_root / module_id
    client_root = module_root / "bin" / "Win64_Shipping_Client"
    server_root = module_root / "bin" / "Win64_Shipping_Server"
    client = runtime_files(client_root)
    server = runtime_files(server_root)
    errors: list[str] = []

    if not client:
        errors.append(f"{module_id}: client presentation runtime is missing or empty at {client_root}")
        return errors
    if server:
        errors.append(
            f"{module_id}: client presentation module must not expose a dedicated-server runtime: "
            + ", ".join(sorted(server))
        )

    try:
        required = declared_dlls(module_root / "SubModule.xml")
    except ValueError as error:
        errors.append(f"{module_id}: {error}")
        required = []
    if not required:
        errors.append(f"{module_id}: SubModule.xml declares no client presentation DLL")
    for dll_name in required:
        if dll_name not in client:
            errors.append(f"{module_id}: advertised client submodule DLL is absent: {dll_name}")

    if not errors:
        print(f"CLIENT PRESENTATION ROLE VERIFIED: {module_id} ({len(client)} files; no server submodule)")
    return errors


def read_role_manifest(path: Path) -> tuple[tuple[str, ...], tuple[str, ...]]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        server_modules = tuple(payload["serverRuntimeModules"])
        client_modules = tuple(payload["clientPresentationModules"])
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise ValueError(f"server module role manifest could not be read: {path}: {error}") from error
    if not server_modules:
        raise ValueError(f"server module role manifest declares no serverRuntimeModules: {path}")
    for role, values in (("serverRuntimeModules", server_modules), ("clientPresentationModules", client_modules)):
        if any(not isinstance(value, str) or not value.strip() for value in values):
            raise ValueError(f"server module role manifest contains an invalid {role} entry: {path}")
        if len({value.casefold() for value in values}) != len(values):
            raise ValueError(f"server module role manifest repeats a {role} entry: {path}")
    overlap = {value.casefold() for value in server_modules} & {value.casefold() for value in client_modules}
    if overlap:
        raise ValueError(f"server module role manifest assigns modules to both roles: {', '.join(sorted(overlap))}")
    return server_modules, client_modules


def verify_support_manifest(modules_root: Path, manifest_path: Path) -> list[str]:
    errors: list[str] = []
    try:
        payload = json.loads(manifest_path.read_text(encoding="utf-8"))
        assemblies = payload["assemblies"]
    except (OSError, ValueError, KeyError, TypeError) as error:
        return [f"dedicated support manifest could not be read: {manifest_path}: {error}"]

    if not isinstance(assemblies, list) or not assemblies:
        return [f"dedicated support manifest contains no assemblies: {manifest_path}"]

    seen: set[str] = set()
    root = modules_root.resolve()
    for entry in assemblies:
        if not isinstance(entry, dict):
            errors.append("dedicated support manifest contains a non-object entry")
            continue
        relative = entry.get("relativePath")
        expected_hash = entry.get("sha256")
        if not isinstance(relative, str) or not relative.strip():
            errors.append("dedicated support manifest contains an empty relativePath")
            continue
        normalized = relative.replace("\\", "/")
        relative_path = Path(normalized)
        if relative_path.is_absolute() or ".." in relative_path.parts:
            errors.append(f"dedicated support path escapes the modules root: {relative}")
            continue
        key = normalized.casefold()
        if key in seen:
            errors.append(f"dedicated support manifest repeats {relative}")
            continue
        seen.add(key)
        if not isinstance(expected_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", expected_hash):
            errors.append(f"dedicated support manifest has an invalid SHA-256 for {relative}")
            continue

        target = (root / relative_path).resolve()
        if root not in target.parents:
            errors.append(f"dedicated support path resolves outside the modules root: {relative}")
        elif not target.is_file():
            errors.append(f"dedicated support assembly is absent: {relative}")
        elif sha256(target) != expected_hash:
            errors.append(f"dedicated support assembly hash differs: {relative}")

    if not errors:
        print(f"SERVER SUPPORT CLOSURE VERIFIED: {len(assemblies)} assemblies")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--modules-root", required=True, type=Path)
    parser.add_argument("--module", action="append", dest="modules")
    parser.add_argument("--client-only-module", action="append", dest="client_only_modules")
    parser.add_argument("--role-manifest", type=Path)
    parser.add_argument("--support-manifest", type=Path)
    args = parser.parse_args()

    if args.role_manifest:
        try:
            modules, client_only_modules = read_role_manifest(args.role_manifest.resolve())
        except ValueError as error:
            print(f"SERVER MODULE BIN VERIFICATION FAILED: {error}", file=sys.stderr)
            return 4
    else:
        modules = tuple(args.modules or DEFAULT_MODULES)
        client_only_modules = tuple(args.client_only_modules or DEFAULT_CLIENT_ONLY_MODULES)
    errors: list[str] = []
    for module_id in modules:
        errors.extend(verify_module(args.modules_root.resolve(), module_id))
    for module_id in client_only_modules:
        errors.extend(verify_client_only_module(args.modules_root.resolve(), module_id))
    if args.support_manifest:
        errors.extend(verify_support_manifest(args.modules_root, args.support_manifest.resolve()))

    if errors:
        for error in errors:
            print(f"SERVER MODULE BIN VERIFICATION FAILED: {error}", file=sys.stderr)
        return 4
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
