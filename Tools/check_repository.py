#!/usr/bin/env python3
"""Fail on repository-layout and public-release hygiene regressions."""

from __future__ import annotations

import filecmp
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SKIP_DIRS = {".git", ".venv", "bin", "Game", "Installer", "Library", "Temp", "work", "__pycache__"}
TEXT_SUFFIXES = {
    ".asset", ".bat", ".cfg", ".cs", ".dat", ".editorconfig", ".gitattributes",
    ".gitignore", ".json", ".md", ".meta", ".obj", ".ps1", ".py", ".sh", ".stl",
    ".terrainlayer", ".txt", ".unity", ".vsp3", ".yaml", ".yml",
}
FORBIDDEN_TEXT = (
    "D:" + chr(92) + "Zcoding",
    "D:/Zcoding",
    "C:" + chr(92) + "Users" + chr(92) + "Lenovo",
    "C:/Users/Lenovo",
    "openvsp-flightsim" + "-dev",
    "-----BEGIN PRIVATE KEY-----",
    "-----BEGIN RSA PRIVATE KEY-----",
    "ghp_",
    "github_pat_",
    "sk-ant-",
)
MAX_FILE_BYTES = 95 * 1024 * 1024


def check_core_copies(errors: list[str]) -> None:
    standalone = ROOT / "FlightDynamics"
    unity = ROOT / "UnityProject" / "Assets" / "Scripts" / "FlightDynamics"
    standalone_files = {p.relative_to(standalone) for p in standalone.rglob("*.cs")}
    standalone_files.discard(Path("IO") / "AircraftConfigJson.cs")
    unity_files = {p.relative_to(unity) for p in unity.rglob("*.cs")}
    if standalone_files != unity_files:
        errors.append("FlightDynamics source lists differ between standalone and Unity copies")
    for relative in sorted(standalone_files & unity_files):
        if not filecmp.cmp(standalone / relative, unity / relative, shallow=False):
            errors.append(f"FlightDynamics copies differ: {relative}")


def check_files(errors: list[str]) -> None:
    for relative in (
        "Data/Workshop/airfoils/Clark Y.dat",
        "Data/Workshop/airfoils/Eppler E205.dat",
    ):
        if (ROOT / relative).is_file():
            errors.append(f"Excluded airfoil data must not be committed: {relative}")
    for relative in (
        "VspImports/LogisticV0928",
        "VspImports/logisitc",
        "VspImports/logisti",
        "VspImports/uav_71770e",
        "VspImports/弹簧刀",
        "Tests/Fixtures/SplitGroups",
    ):
        if (ROOT / relative).exists():
            errors.append(f"Excluded model or derived fixture must not be committed: {relative}")
    for path in ROOT.rglob("*"):
        if any(part in SKIP_DIRS for part in path.relative_to(ROOT).parts):
            continue
        if not path.is_file():
            continue
        relative = path.relative_to(ROOT)
        if path.suffix in {".pyc", ".pyo"}:
            errors.append(f"Python cache must not be committed: {relative}")
        size = path.stat().st_size
        if size > MAX_FILE_BYTES:
            errors.append(f"File exceeds 95 MiB: {relative} ({size} bytes)")
        if path.resolve() == Path(__file__).resolve():
            continue
        if path.suffix.lower() not in TEXT_SUFFIXES and path.name not in {"LICENSE", "NOTICE"}:
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        for token in FORBIDDEN_TEXT:
            if token in text:
                errors.append(f"Forbidden local path or credential marker in {relative}: {token!r}")


def check_markdown_links(errors: list[str]) -> None:
    for path in ROOT.rglob("*.md"):
        if any(part in SKIP_DIRS for part in path.relative_to(ROOT).parts):
            continue
        text = path.read_text(encoding="utf-8")
        for target in re.findall(r"\[[^\]]*\]\(([^)]+)\)", text):
            target = target.strip().split("#", 1)[0]
            if not target or "://" in target or target.startswith("mailto:"):
                continue
            if not (path.parent / target).exists():
                errors.append(f"Broken Markdown link in {path.relative_to(ROOT)}: {target}")


def main() -> int:
    errors: list[str] = []
    check_core_copies(errors)
    check_files(errors)
    check_markdown_links(errors)
    if errors:
        print("Repository checks failed:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1
    print("Repository checks passed: synchronized core, clean paths, valid local links, no caches or oversized files.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
