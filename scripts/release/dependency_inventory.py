#!/usr/bin/env python3
"""Reviewable NuGet dependency and declared-license inventory from locked restores."""

import argparse
import base64
import binascii
import difflib
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
INVENTORY = Path("docs/dependencies/nuget-inventory.json")


def children(element, name):
    return [child for child in element.iter() if child.tag.rsplit("}", 1)[-1] == name]


def package_license(cache, package, version):
    path = cache / package.lower() / version.lower() / f"{package.lower()}.nuspec"
    if not path.is_file():
        raise ValueError(f"Missing restored package manifest: {package} {version}")

    metadata = children(ET.parse(path).getroot(), "metadata")
    if len(metadata) != 1:
        raise ValueError(f"Invalid package metadata: {package} {version}")

    license_tags = children(metadata[0], "license")
    if license_tags:
        tag = license_tags[0]
        kind = tag.attrib.get("type", "unknown")
        if kind not in ("expression", "file") or not (tag.text or "").strip():
            raise ValueError(f"Invalid declared package license: {package} {version}")
        return {"kind": kind, "value": tag.text.strip()}

    legacy = children(metadata[0], "licenseUrl")
    if legacy and (legacy[0].text or "").strip():
        return {"kind": "url", "value": legacy[0].text.strip()}

    return {"kind": "unknown", "value": ""}


def generate(root=ROOT, cache=None):
    root = Path(root)
    cache = Path(cache or os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
    global_settings = json.loads((root / "global.json").read_text(encoding="utf-8"))["sdk"]
    if global_settings.get("rollForward") != "disable":
        raise ValueError("Exact SDK selection is required for the dependency inventory.")

    central = {}
    for item in children(ET.parse(root / "Directory.Packages.props").getroot(), "PackageVersion"):
        name, version = item.attrib["Include"], item.attrib["Version"]
        if name.lower() in central:
            raise ValueError(f"Duplicate central package version: {name}")
        central[name.lower()] = version

    solution = ET.parse(root / "Praxis.slnx").getroot()
    projects = sorted({item.attrib["Path"] for item in children(solution, "Project")})
    if not projects:
        raise ValueError("The solution contains no projects.")

    package_rows = {}
    for project in projects:
        project_path = root / project
        name = Path(project).parent.as_posix()
        model = ET.parse(project_path).getroot()
        direct = {item.attrib["Include"].lower() for item in children(model, "PackageReference")}
        linked = {Path(item.attrib["Include"].replace("\\", "/")).stem.lower()
                  for item in children(model, "ProjectReference")}
        if not direct.issubset(central):
            raise ValueError(f"Project has unversioned package references: {name}")

        lock_path = project_path.with_name("packages.lock.json")
        if not lock_path.is_file():
            raise ValueError(f"Missing package lockfile: {lock_path.relative_to(root)}")
        lock = json.loads(lock_path.read_text(encoding="utf-8"))
        targets = lock.get("dependencies", {})
        if lock.get("version") != 2 or set(targets) != {"net10.0"}:
            raise ValueError(f"Unexpected lockfile format or target framework: {name}")

        seen_direct, seen_linked = set(), set()
        for package, item in targets["net10.0"].items():
            lower = package.lower()
            kind = item.get("type")
            if kind == "Project":
                if lower not in linked:
                    raise ValueError(f"Unexpected project dependency in {name}: {package}")
                seen_linked.add(lower)
                continue

            if kind not in ("Direct", "Transitive"):
                raise ValueError(f"Invalid dependency kind in {name}: {package}")
            if (lower in direct) != (kind == "Direct"):
                raise ValueError(f"Direct dependency mismatch in {name}: {package}")
            if kind == "Direct":
                if item.get("resolved") != central[lower]:
                    raise ValueError(f"Central version and lockfile disagree in {name}: {package}")
                seen_direct.add(lower)

            version = item.get("resolved")
            digest = item.get("contentHash")
            if not isinstance(version, str) or not isinstance(digest, str):
                raise ValueError(f"Unresolved package in {name}: {package}")
            try:
                decoded = base64.b64decode(digest, validate=True)
            except binascii.Error as error:
                raise ValueError(f"Invalid package content hash in {name}: {package}") from error
            if len(decoded) != 64:
                raise ValueError(f"Invalid SHA-512 length in {name}: {package}")

            key = (lower, version)
            previous = package_rows.get(key)
            if previous is not None:
                if previous["contentHash"] != digest:
                    raise ValueError(f"Conflicting hashes for {package} {version}")
                previous["projects"].add(name)
            else:
                package_rows[key] = {
                    "package": package,
                    "version": version,
                    "contentHash": digest,
                    "projects": {name},
                    "declaredLicense": package_license(cache, package, version),
                }

        if seen_direct != direct or seen_linked != linked:
            raise ValueError(f"Missing direct/project dependencies in {name}")

    packages = sorted(package_rows.values(), key=lambda row: (row["package"].lower(), row["version"]))
    for row in packages:
        row["projects"] = sorted(row["projects"])
    return {"schemaVersion": 1, "sdk": global_settings["version"], "packages": packages}


def serialized(inventory):
    return (json.dumps(inventory, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def main():
    parser = argparse.ArgumentParser()
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--check", action="store_true")
    action.add_argument("--write", action="store_true")
    action.add_argument("--emit-base64", action="store_true")
    args = parser.parse_args()

    report = serialized(generate())
    if args.emit_base64:
        print("PRAXIS_DEPENDENCY_INVENTORY=" + base64.b64encode(report).decode("ascii"))
    elif args.write:
        destination = ROOT / INVENTORY
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(report)
    else:
        path = ROOT / INVENTORY
        if not path.is_file() or path.read_bytes() != report:
            existing = path.read_text(encoding="utf-8") if path.is_file() else ""
            changes = difflib.unified_diff(existing.splitlines(), report.decode("utf-8").splitlines(),
                                           fromfile=str(INVENTORY), tofile="restored-dependencies", lineterm="")
            raise SystemExit("NuGet dependency/license inventory is out of date:\n" +
                             "\n".join(list(changes)[:80]))

    output = json.loads(report)
    unknown = [row["package"] for row in output["packages"]
               if row["declaredLicense"]["kind"] == "unknown"]
    print(f"Inventory: {len(output['packages'])} locked packages, "
          f"{len(unknown)} without declared license metadata.")
    if unknown:
        print("Packages requiring explicit license investigation: " + ", ".join(unknown))


if __name__ == "__main__":
    main()
