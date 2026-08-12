#!/usr/bin/env python3
"""Validate release triggers and the public launcher configuration."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re

import yaml


ROOT = Path(__file__).resolve().parents[2]


def load_workflow(name: str) -> dict:
    with (ROOT / ".github" / "workflows" / name).open(encoding="utf-8") as stream:
        return yaml.load(stream, Loader=yaml.BaseLoader)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def step_names(workflow: dict, job: str) -> set[str]:
    return {step.get("name", "") for step in workflow["jobs"][job]["steps"]}


def validate_workshop_receipt() -> None:
    manifest = json.loads((ROOT / "deploy" / "workshop-mods.json").read_text(encoding="utf-8"))
    receipt_path = ROOT / "deploy" / "WorkshopSuite" / "MANIFEST.json"
    require(receipt_path.is_file(), "client payload source must include the managed Workshop receipt")
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))

    expected = {
        module["moduleId"]: {
            key: module[key]
            for key in ("moduleId", "workshopId", "steamManifestId", "version", "loadOrder")
        }
        for module in manifest["modules"]
    }
    actual = {
        module["moduleId"]: {
            key: module[key]
            for key in ("moduleId", "workshopId", "steamManifestId", "version", "loadOrder")
        }
        for module in receipt["modules"]
    }
    require(receipt["schemaVersion"] == 1, "managed Workshop receipt schema must be version 1")
    require(receipt["suiteId"] == manifest["suite"]["id"], "managed Workshop suite identity drifted")
    require(
        receipt["moduleCount"] == manifest["suite"]["expectedModuleCount"] == len(actual),
        "managed Workshop receipt count drifted from the production suite",
    )
    require(actual == expected, "managed Workshop receipt identities drifted from the production suite")

    canonical = "\n".join(
        "|".join(
            (
                module["moduleId"].lower(),
                module["workshopId"],
                module["steamManifestId"],
                module["version"],
                str(module["loadOrder"]),
                module["contentSha256"].lower(),
                module["configurationSha256"].lower(),
            )
        )
        for module in sorted(receipt["modules"], key=lambda item: item["moduleId"].lower())
    )
    require(
        hashlib.sha256(canonical.encode("utf-8")).hexdigest() == receipt["receiptSha256"],
        "managed Workshop receipt digest is invalid",
    )


def main() -> None:
    client = load_workflow("launcher-release.yml")
    client_triggers = client["on"]
    require(
        client_triggers["push"]["branches"] == ["development"],
        "client feed may auto-publish only from development",
    )
    require(
        client_triggers["workflow_dispatch"]["inputs"]["channels"]["default"] == "nightly",
        "manual client releases must default to nightly",
    )
    channels = client["jobs"]["publish"]["steps"][-1]["env"]["CHANNELS"]
    require("'nightly'" in channels and "'both'" not in channels, "pushes must never publish stable")
    require("safety" in client["jobs"], "client release must have a release-safety gate")
    require(client["jobs"]["build"].get("needs") == "safety", "client build must wait for release-safety")
    client_workflow_text = (ROOT / ".github" / "workflows" / "launcher-release.yml").read_text(
        encoding="utf-8"
    )
    require(
        "deploy/WorkshopSuite/MANIFEST.json" in client_workflow_text
        and '"$module/WorkshopSuite/MANIFEST.json"' in client_workflow_text,
        "client release must package the managed Workshop receipt",
    )
    validate_workshop_receipt()

    app = load_workflow("launcher-app-release.yml")
    app_triggers = app["on"]
    require(
        app_triggers["push"]["branches"] == ["development"],
        "launcher pushes may auto-publish only from development",
    )
    app_dispatch = app_triggers["workflow_dispatch"]["inputs"]["channel"]
    require(app_dispatch["default"] == "nightly", "manual launcher releases must default to nightly")
    require(
        app_dispatch["options"] == ["nightly", "stable"],
        "launcher release choices must be nightly or stable",
    )
    require("safety" in app["jobs"], "launcher app release must have a release-safety gate")
    require(app["jobs"]["build"].get("needs") == "safety", "launcher app build must wait for release-safety")
    required_app_steps = {"Run launcher safety tests", "Check XAML resources"}
    require(
        required_app_steps.issubset(step_names(app, "build")),
        "launcher publication must run config and XAML regression tests",
    )
    app_workflow_text = (ROOT / ".github" / "workflows" / "launcher-app-release.yml").read_text(
        encoding="utf-8"
    )
    require(
        "launcher-nightly" in app_workflow_text and "launcher-app" in app_workflow_text,
        "launcher workflow must publish distinct nightly and stable tags",
    )
    require(
        "launcher.json" in app_workflow_text and "sha256sum" in app_workflow_text,
        "launcher workflow must publish a SHA-256-pinned self-update manifest",
    )
    require(
        "github.event_name == 'push'" in app_workflow_text,
        "launcher development pushes must be forced to nightly",
    )

    config_text = (ROOT / "tools" / "CoopLauncher" / "launcher-config.json").read_text(encoding="utf-8")
    config_text = re.sub(r"^\s*//.*$", "", config_text, flags=re.MULTILINE)
    public_config = json.loads(config_text)
    require(public_config.get("serverPassword") == "", "public launcher config must not contain a join password")
    require(
        public_config.get("launcherManifestUrl", "").endswith("/launcher-app/launcher.json"),
        "public launcher config must use the stable self-update feed",
    )

    print(
        "PASS: stable feeds are manual, pushes are nightly-only, launcher manifests are pinned, "
        "the managed Workshop receipt is pinned, and public config has no password"
    )


if __name__ == "__main__":
    main()
