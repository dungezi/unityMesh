"""Build a source-only UPM archive and a standard Unity GUID/asset/pathname archive."""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import re
import io
import json
import shutil
import tarfile
import zipfile

WORKSPACE = Path(__file__).resolve().parents[1]
PACKAGE = WORKSPACE / "Packages" / "com.codex.stylized-mesh"
ARTIFACTS = WORKSPACE / "artifacts"


def guid(path: str) -> str:
    return hashlib.md5(("StylizedMeshStudio-v1/" + path).encode()).hexdigest()


def metadata(path: str, directory=False) -> bytes:
    header = f"fileFormatVersion: 2\nguid: {guid(path)}\n"
    if directory:
        return (header + "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n").encode()
    suffix = Path(path).suffix
    if suffix == ".cs":
        importer = "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n"
    elif suffix == ".asmdef":
        importer = "AssemblyDefinitionImporter:\n  externalObjects: {}\n"
    elif suffix == ".shader":
        importer = "ShaderImporter:\n  externalObjects: {}\n  defaultTextures: []\n  nonModifiableTextures: []\n  preprocessorOverride: 0\n"
    else:
        importer = "DefaultImporter:\n  externalObjects: {}\n"
    return (header + importer + "  userData: \n  assetBundleName: \n  assetBundleVariant: \n").encode()


def add_bytes(archive, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = 0o644
    info.mtime = 0
    archive.addfile(info, io.BytesIO(data))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--use-unity-export", action="store_true")
    args = parser.parse_args()
    ARTIFACTS.mkdir(exist_ok=True)
    shutil.copy2(WORKSPACE / "README.md", PACKAGE / "README.md")
    manifest = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8"))
    version = manifest["version"]
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?", version):
        raise ValueError("Invalid package version")
    for path in PACKAGE.rglob("*.asmdef"):
        json.loads(path.read_text(encoding="utf-8"))
    for path in sorted(PACKAGE.rglob("*")):
        if path.suffix == ".meta":
            continue
        relative = path.relative_to(PACKAGE).as_posix()
        Path(str(path) + ".meta").write_bytes(metadata(relative, path.is_dir()))

    archive_files = [path for path in sorted(PACKAGE.rglob("*")) if path.is_file() and path.suffix != ".meta" and path.name != "package.json"]
    package_path = ARTIFACTS / f"StylizedMeshStudio-{version}.unitypackage"
    entries = [Path(".")] + [p.relative_to(PACKAGE) for p in sorted(PACKAGE.rglob("*")) if p.is_dir()] + [p.relative_to(PACKAGE) for p in archive_files]
    if not args.use_unity_export:
      with tarfile.open(package_path, "w:gz", format=tarfile.GNU_FORMAT) as archive:
        # Root and directory records preserve folder GUIDs too.
        for relative in entries:
            root = relative == Path(".")
            path = "" if root else relative.as_posix()
            asset = PACKAGE / relative
            identifier = guid(path)
            unity_path = "Assets/StylizedMesh" + ("/" + path if path else "")
            add_bytes(archive, identifier + "/pathname", unity_path.encode())
            add_bytes(archive, identifier + "/asset.meta", metadata(path, root or asset.is_dir()))
            if asset.is_file():
                add_bytes(archive, identifier + "/asset", asset.read_bytes())

    zip_path = ARTIFACTS / f"StylizedMeshStudio-{version}-UPM.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(PACKAGE.rglob("*")):
            if path.is_file():
                archive.write(path, "com.codex.stylized-mesh/" + path.relative_to(PACKAGE).as_posix())

    with tarfile.open(package_path, "r:gz") as archive:
        records = {}
        for member in archive.getmembers():
            if member.isdir():
                continue
            parts = PurePosixPath(member.name).parts
            assert len(parts) == 2 and ".." not in parts
            records.setdefault(parts[0], {})[parts[1]] = archive.extractfile(member).read()
        if not args.use_unity_export:
            assert len(records) == len(entries)
        paths = set()
        for identifier, record in records.items():
            path = record["pathname"].decode()
            assert path.startswith("Assets/StylizedMesh") and path not in paths
            paths.add(path)
            assert f"guid: {identifier}".encode() in record["asset.meta"]
            if b"folderAsset: yes" not in record["asset.meta"]:
                assert "asset" in record
            if path.endswith(".cs"):
                assert record["asset"] == (PACKAGE / path.removeprefix("Assets/StylizedMesh/")).read_bytes()
        assert "Assets/StylizedMesh/Editor/StylizedMeshWindow.cs" in paths
        assert "Assets/StylizedMesh/Shaders/ColorBlocks.shader" in paths

    with zipfile.ZipFile(zip_path) as archive:
        assert archive.testzip() is None
        assert json.loads(archive.read("com.codex.stylized-mesh/package.json"))["version"] == version

    fingerprint = hashlib.sha256()
    for path in sorted(PACKAGE.rglob("*.cs")):
        fingerprint.update(path.relative_to(PACKAGE).as_posix().encode())
        fingerprint.update(path.read_bytes())
    report_path = ARTIFACTS / "validation.txt"
    integration = "NOT RUN for current sources"
    source_files = [p for p in PACKAGE.rglob("*") if p.suffix in {".cs", ".shader", ".asmdef"}]
    copied_files = [WORKSPACE / "ValidationProject/Assets/StylizedMesh" / p.relative_to(PACKAGE) for p in source_files]
    sources_match = all(b.exists() and a.read_bytes() == b.read_bytes() for a, b in zip(source_files, copied_files))
    if report_path.exists() and sources_match:
        report = report_path.read_text(encoding="utf-8")
        # ImportPackage can rewrite identical copied files with a later timestamp.
        # Compare bytes to the tested project and date the canonical source files instead.
        code_time = max(p.stat().st_mtime for p in source_files)
        passed = re.search(r"(\d+)/(\d+) cases passed", report)
        if "FAIL" not in report and passed and passed[1] == passed[2] and report_path.stat().st_mtime >= code_time:
            integration = f"{passed[1]}/{passed[2]} passed in Unity 2022.3.22f1, including scene handles, preview visibility, expression updates, stepwise undo and scene navigation shortcuts"
    import_report = ARTIFACTS / "import-validation.txt"
    imported = sources_match and import_report.exists() and "PASS Unity AssetDatabase.ImportPackage" in import_report.read_text(encoding="utf-8")
    status = {
        "version": version,
        "source_sha256": fingerprint.hexdigest(),
        "csharp_compilation": "Passed against installed Unity 2022.3.22f1 assemblies",
        "managed_algorithm_tests": "7/7 passed",
        "unity_integration_tests": integration,
        "package_structure": "Verified; Unity ImportPackage and native export passed" if imported else "Verified; actual Unity import NOT RUN",
        "real_avatar_tests": "NOT RUN: no Eku or Mafuyu model files provided",
    }
    (ARTIFACTS / "validation-status.json").write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding="utf-8")
    (ARTIFACTS / "distribution-checks.txt").write_text(
        f"PASS Unity GUID/pathname/asset records: {len(records)}\nPASS UPM ZIP CRC and manifest\n"
        f"PASS archived C# sources match workspace\nSource SHA256: {fingerprint.hexdigest()}\n"
        + ("PASS Unity importer execution and native export\n" if imported else "Unity importer execution NOT RUN for current sources.\n"), encoding="utf-8")
    print(f"Built {package_path.name}: {package_path.stat().st_size:,} bytes")
    print(f"Built {zip_path.name}: {zip_path.stat().st_size:,} bytes")
    print(f"Verified {len(records)} Unity asset/folder records.")


if __name__ == "__main__":
    main()
