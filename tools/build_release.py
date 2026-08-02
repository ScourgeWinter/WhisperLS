"""Costruisce gli asset win-x64 ufficiali di WhisperLS senza modificare il sistema."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

PYTHON_VERSION = "3.12.10"
PYTHON_EMBED_URL = (
    f"https://www.python.org/ftp/python/{PYTHON_VERSION}/"
    f"python-{PYTHON_VERSION}-embed-amd64.zip"
)


def run(*arguments: str, cwd: Path) -> None:
    subprocess.run(arguments, cwd=cwd, check=True)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def build_runtime(root: Path, destination: Path, full: bool) -> None:
    archive = destination.parent / "python-embed.zip"
    urllib.request.urlretrieve(PYTHON_EMBED_URL, archive)
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as package:
        package.extractall(destination)
    archive.unlink()
    pth_path = next(destination.glob("python*._pth"))
    pth_path.write_text(
        pth_path.read_text(encoding="utf-8").replace("#import site", "import site")
        + "\nLib\\site-packages\n",
        encoding="utf-8",
    )
    site_packages = destination / "Lib" / "site-packages"
    requirements = [str(root / "requirements.txt")]
    if full:
        requirements.append(str(root / "requirements-full.txt"))
    command = [
        sys.executable,
        "-m",
        "pip",
        "install",
        "--disable-pip-version-check",
        "--only-binary=:all:",
        "--target",
        str(site_packages),
    ]
    for requirement in requirements:
        command.extend(["-r", requirement])
    run(*command, cwd=root)


def publish_app(root: Path, destination: Path) -> None:
    updater_project = root / "WhisperLS.Updater" / "WhisperLS.Updater.csproj"
    gui_project = root / "FasterWhisperGui" / "FasterWhisperGui.csproj"
    updater_output = root / "artifacts" / "updater"
    run(
        "dotnet", "publish", str(updater_project), "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-o", str(updater_output), cwd=root,
    )
    run(
        "dotnet", "publish", str(gui_project), "-c", "Release", "-p:Platform=x64",
        "-r", "win-x64", "--self-contained", "true", "-o", str(destination), cwd=root,
    )
    shutil.copy2(updater_output / "WhisperLS.Updater.exe", destination / "WhisperLS.Updater.exe")


def make_zip(source: Path, destination: Path) -> None:
    if destination.exists():
        destination.unlink()
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(source.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(source))


def write_update_manifest(
    artifacts: Path,
    source: Path,
    version: str,
    variant: str,
) -> Path:
    payload = artifacts / f"WhisperLS-Update-{version}-{variant}-x64.zip"
    make_zip(source, payload)
    manifest = {
        "version": version,
        "repository": "ScourgeWinter/WhisperLS",
        "payloadSha256": sha256(payload),
        "files": [
            str(path.relative_to(source)).replace("\\", "/")
            for path in sorted(source.rglob("*")) if path.is_file()
        ],
        "removeFiles": [],
    }
    manifest_path = artifacts / f"update-manifest-{variant.lower()}.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return payload


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default="1.0.0")
    parser.add_argument("--skip-installers", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    artifacts = root / "artifacts"
    if artifacts.exists():
        shutil.rmtree(artifacts)
    artifacts.mkdir()

    light = artifacts / "app-light"
    full = artifacts / "app-full"
    publish_app(root, light)
    shutil.copytree(light, full)
    build_runtime(root, light / "Runtime", full=False)
    build_runtime(root, full / "Runtime", full=True)
    (light / "distribution.json").write_text(
        json.dumps({"variant": "Light"}, indent=2), encoding="utf-8"
    )
    (full / "distribution.json").write_text(
        json.dumps({"variant": "Full"}, indent=2), encoding="utf-8"
    )

    portable = artifacts / f"WhisperLS-Portable-{args.version}-Full-x64.zip"
    make_zip(full, portable)
    write_update_manifest(artifacts, full, args.version, "Full")
    write_update_manifest(artifacts, light, args.version, "Light")

    if not args.skip_installers:
        iscc = shutil.which("ISCC.exe") or shutil.which("iscc")
        if not iscc:
            local_iscc = (
                Path.home() / "AppData" / "Local" / "Programs" /
                "Inno Setup 6" / "ISCC.exe"
            )
            iscc = str(local_iscc) if local_iscc.is_file() else None
        if not iscc:
            raise RuntimeError("Inno Setup (ISCC.exe) non è disponibile.")
        for variant, source in (("Full", full), ("Light", light)):
            run(
                iscc,
                f"/DAppVersion={args.version}",
                f"/DVariant={variant}",
                f"/DAppSource={source}",
                f"/DOutputDirectory={artifacts}",
                str(root / "installer" / "WhisperLS.iss"),
                cwd=root,
            )

    checksums = []
    for path in sorted(artifacts.iterdir()):
        if path.is_file() and path.name != "SHA256SUMS.txt":
            checksums.append(f"{sha256(path)}  {path.name}")
    (artifacts / "SHA256SUMS.txt").write_text("\n".join(checksums) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
