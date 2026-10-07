"""Scan the staged Git contents and selected release archives without printing matched secrets."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
CHECKS = {
    "GitHub credential": r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,})\b",
    "Private key": r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----",
    "Cloud key": r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b",
    "Personal email": r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
    "Windows user directory": r"(?i)[A-Z]:[\\/]+Users[\\/]+[^\s\"<>]+",
    "Local workspace directory": r"(?i)[A-Z]:[\\/]+GitProject[\\/]+[^\s\"<>]+",
    "Secret value assignment": r"(?i)\b(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[:=]\s*[\"']([^\"'\s]{12,})[\"']",
}
PATTERNS = {label: re.compile(pattern) for label, pattern in CHECKS.items()}


def scan(name, data, findings):
    if b"\x00" in data:
        return
    text = data.decode("utf-8-sig", errors="replace")
    for label, pattern in PATTERNS.items():
        for match in pattern.finditer(text):
            # Upstream public author attribution is part of the retained MIT source notices.
            if label == "Personal email" and "/ThirdParty/" in name:
                continue
            if label == "Personal email" and match.group(0).endswith("@users.noreply.github.com"):
                continue
            findings.append({"file": name, "line": text.count("\n", 0, match.start()) + 1, "category": label})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archives", action="store_true")
    args = parser.parse_args()
    paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode("utf-8").strip("\x00").split("\x00")
    findings = []
    for name in paths:
        data = subprocess.check_output(["git", "show", ":" + name], cwd=ROOT)
        scan(name, data, findings)
    archive_files = 0
    if args.archives:
        version = json.loads((ROOT / "Packages/com.codex.stylized-mesh/package.json").read_text())["version"]
        with zipfile.ZipFile(ROOT / f"artifacts/StylizedMeshStudio-{version}-UPM.zip") as archive:
            for name in archive.namelist():
                if not name.endswith("/"):
                    scan("UPM:" + name, archive.read(name), findings)
                    archive_files += 1
        with tarfile.open(ROOT / f"artifacts/StylizedMeshStudio-{version}.unitypackage") as archive:
            asset_paths = {member.name.rsplit("/", 1)[0]: archive.extractfile(member).read().decode("utf-8")
                           for member in archive.getmembers() if member.isfile() and member.name.endswith("/pathname")}
            for member in archive.getmembers():
                if member.isfile():
                    logical = asset_paths.get(member.name.rsplit("/", 1)[0], member.name)
                    scan("UnityPackage:" + logical, archive.extractfile(member).read(), findings)
                    archive_files += 1
    report = {"staged_files_scanned": len(paths), "archive_entries_scanned": archive_files,
              "findings": findings, "result": "PASS" if not findings else "REVIEW REQUIRED",
              "scope": "Pattern-based review of staged text and release archive contents; no matched values are logged."}
    (ROOT / "artifacts/privacy-checks.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    raise SystemExit(1 if findings else 0)


if __name__ == "__main__":
    main()
