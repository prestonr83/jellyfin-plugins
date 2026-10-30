#!/usr/bin/env python3
"""Build/test catalog plugins and create deterministic Jellyfin release assets. Python stdlib only."""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import urllib.parse
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
IGNORED = {"bin", "obj", ".git", ".work", ".tools", "artifacts", "__pycache__"}


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def version_key(value: str) -> tuple[int, ...]:
    if not isinstance(value, str) or not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", value):
        raise ValueError(f"Expected a four-part plugin/ABI version: {value!r}")
    parts = tuple(map(int, value.split(".")))
    if any(part > 65535 for part in parts):
        raise ValueError(f"Version component exceeds assembly version range: {value}")
    return parts


def repository_name(value: str) -> str:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+", value) or value.split("/")[-1] in (".", ".."):
        raise ValueError("GitHub repository must be owner/name.")
    return value


def inside(root: Path, relative: str) -> Path:
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError(f"Path leaves the repository: {relative}")
    return path


def load_configuration(root: Path = ROOT) -> dict:
    config = read_json(root / "repository.json")
    if config.get("schemaVersion") != 1 or not config.get("plugins"):
        raise ValueError("Unsupported or empty repository configuration.")
    repository_name(config["githubRepository"])
    ids: set[str] = set()
    directories: set[str] = set()
    for plugin in config["plugins"]:
        directory = inside(root, plugin["directory"])
        if plugin["directory"] in directories:
            raise ValueError("A plugin directory is configured twice.")
        directories.add(plugin["directory"])
        meta = read_json(directory / "meta.json")
        plugin_id = str(uuid.UUID(meta["guid"]))
        if plugin_id in ids:
            raise ValueError("Plugin GUIDs must be unique.")
        ids.add(plugin_id)
        version_key(meta["version"])
        abi = version_key(meta["targetAbi"])
        if abi[:3] != tuple(map(int, plugin["jellyfinVersion"].split("."))):
            raise ValueError(f"{meta['name']}: ABI must match the build's Jellyfin package baseline.")
        for field in ("name", "description", "overview", "owner", "category"):
            if not meta.get(field):
                raise ValueError(f"{meta['name']}: missing metadata field {field}.")
        if meta.get("autoUpdate") is not True:
            raise ValueError(f"{meta['name']}: distributed packages must allow automatic updates.")
        for field in ("solution", "project"):
            if not inside(directory, plugin[field]).is_file():
                raise ValueError(f"Missing {field} for {meta['name']}.")
        if Path(plugin["assembly"]).name != plugin["assembly"] or not plugin["assembly"].endswith(".dll"):
            raise ValueError("Assembly must be a DLL filename, without a directory.")
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*", plugin["archivePrefix"]):
            raise ValueError("Archive prefix must be a simple filename prefix.")
        for dependency in plugin.get("runtimeAssemblies", []):
            if Path(dependency).name != dependency or "/" in dependency or "\\" in dependency or not dependency.endswith(".dll"):
                raise ValueError("Runtime dependencies must be explicit DLL filenames.")
            if dependency.startswith(("Jellyfin.", "MediaBrowser.", "Microsoft.", "System.")):
                raise ValueError("Server/framework DLLs must not be shipped as plugin dependencies.")
    return config


def source_checksum(root: Path, plugin: dict) -> str:
    directory = inside(root, plugin["directory"])
    project = inside(directory, plugin["project"])
    files = [directory / "meta.json"]
    files += [path for path in project.parent.rglob("*") if path.is_file()
              and not any(part in IGNORED for part in path.relative_to(project.parent).parts)]
    for name in ("Directory.Build.props", "Directory.Build.targets", "global.json"):
        for parent in (directory, root):
            if (parent / name).is_file():
                files.append(parent / name)
    digest = hashlib.sha256()
    digest.update(plugin["jellyfinVersion"].encode("utf-8") + b"\0")
    digest.update(json.dumps(plugin.get("runtimeAssemblies", []), sort_keys=True).encode("utf-8") + b"\0")
    for path in sorted(set(files), key=lambda item: item.relative_to(root).as_posix()):
        digest.update(path.relative_to(root).as_posix().encode("utf-8") + b"\0")
        content = path.read_bytes()
        if path.suffix.lower() in {".cs", ".csproj", ".props", ".targets", ".json", ".html", ".sql", ".xml", ".config", ".resx", ".txt", ".yaml", ".yml", ".svg"}:
            content = content.replace(b"\r\n", b"\n")
        digest.update(content + b"\0")
    return digest.hexdigest()


def validate_catalog(catalog: list) -> None:
    if not isinstance(catalog, list):
        raise ValueError("A Jellyfin catalog must be a JSON array.")
    ids: set[str] = set()
    for plugin in catalog:
        plugin_id = str(uuid.UUID(plugin["guid"]))
        if plugin_id in ids:
            raise ValueError("Duplicate plugin GUID in catalog.")
        ids.add(plugin_id)
        versions = set()
        for item in plugin["versions"]:
            key = (version_key(item["version"]), version_key(item["targetAbi"]))
            if key in versions:
                raise ValueError("Duplicate version/ABI in catalog.")
            versions.add(key)
            if not re.fullmatch(r"[0-9a-f]{32}", item["checksum"]):
                raise ValueError("Jellyfin download checksums must be lowercase MD5.")
            url = urllib.parse.urlsplit(item["sourceUrl"])
            if url.scheme != "https" or not url.netloc or url.username or url.password:
                raise ValueError("Package URLs must be public HTTPS URLs without credentials.")
            dt.datetime.fromisoformat(item["timestamp"].replace("Z", "+00:00"))


def deterministic_zip(path: Path, entries: dict[str, bytes]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, content in sorted(entries.items()):
            if Path(name).name != name or "/" in name or "\\" in name:
                raise ValueError("Plugin archive entries must be flat filenames.")
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, content, compresslevel=9)


def find_published(previous: list, plugin_id: str, version: str, abi: str) -> dict | None:
    for plugin in previous:
        if plugin["guid"].lower() == plugin_id.lower():
            return next((item for item in plugin["versions"]
                         if item["version"] == version and item["targetAbi"] == abi), None)
    return None


def create_catalog(root: Path, config: dict, repository: str, tag: str, output: Path,
                   previous: list, timestamp: str) -> tuple[list, list]:
    repository_name(repository)
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", tag):
        raise ValueError("Release tag must contain only letters, numbers, dots, underscores and hyphens.")
    validate_catalog(previous)
    previous_by_id = {plugin["guid"].lower(): plugin for plugin in previous}
    catalog = []
    summary = []
    for spec in config["plugins"]:
        directory = inside(root, spec["directory"])
        meta = read_json(directory / "meta.json")
        fingerprint = source_checksum(root, spec)
        old = find_published(previous, meta["guid"], meta["version"], meta["targetAbi"])
        if old:
            if old.get("sourceChecksum") != fingerprint:
                raise ValueError(f"{meta['name']} {meta['version']} is already published with different inputs; bump its version before releasing.")
            entry = dict(old)
            summary.append({"name": meta["name"], "version": meta["version"], "reused": True,
                            "sourceUrl": old["sourceUrl"]})
        else:
            assembly_directory = (directory / "artifacts/catalog-publish") if spec.get("publish") else \
                inside(directory, spec["project"]).parent / "bin" / "Release" / "net10.0"
            assembly = assembly_directory / spec["assembly"]
            if not assembly.is_file():
                raise ValueError(f"Build output is missing: {assembly}")
            filename = f"{spec['archivePrefix']}_{meta['version']}_jellyfin{spec['jellyfinVersion']}.zip"
            package = output / filename
            contents = {name: (assembly_directory / name).read_bytes()
                        for name in [spec["assembly"], *spec.get("runtimeAssemblies", [])]}
            contents["meta.json"] = (json.dumps(meta, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
            deterministic_zip(package, contents)
            content = package.read_bytes()
            checksum = hashlib.md5(content, usedforsecurity=False).hexdigest()
            sha256 = hashlib.sha256(content).hexdigest()
            package.with_suffix(package.suffix + ".sha256").write_text(f"{sha256}  {filename}\n", encoding="ascii")
            entry = {"version": meta["version"], "changelog": spec["changelog"],
                "targetAbi": meta["targetAbi"],
                "sourceUrl": f"https://github.com/{repository}/releases/download/{urllib.parse.quote(tag, safe='')}/{filename}",
                "checksum": checksum, "timestamp": timestamp, "sourceChecksum": fingerprint}
            summary.append({"name": meta["name"], "version": meta["version"], "reused": False,
                            "file": filename, "md5": checksum, "sha256": sha256, "sourceUrl": entry["sourceUrl"]})
        old_versions = previous_by_id.get(meta["guid"].lower(), {}).get("versions", [])
        versions = [dict(item) for item in old_versions if (item["version"], item["targetAbi"]) != (entry["version"], entry["targetAbi"])]
        versions.append(entry)
        versions.sort(key=lambda item: (version_key(item["version"]), version_key(item["targetAbi"])), reverse=True)
        catalog.append({key: meta[key] for key in ("guid", "name", "description", "overview", "owner", "category")}
                       | {"versions": versions})
    # Retain retired plugins/older ABI lines so existing installations can still find compatible releases.
    current_ids = {plugin["guid"].lower() for plugin in catalog}
    catalog.extend(plugin for plugin in previous if plugin["guid"].lower() not in current_ids)
    validate_catalog(catalog)
    write_json(output / "manifest.json", catalog)
    write_json(output / "release-summary.json", summary)
    notes = [f"# {config['name']} — {tag}", "", "Add this repository URL in Jellyfin:", "",
             f"`https://github.com/{repository}/releases/latest/download/manifest.json`", ""]
    for item in summary:
        notes.append(f"- {item['name']} {item['version']}" + (" (existing immutable release retained)" if item["reused"] else ""))
    (output / "release-notes.md").write_text("\n".join(notes) + "\n", encoding="utf-8")
    return catalog, summary


def prepare_native_source(config: dict, supplied: str | None) -> Path:
    if supplied:
        root = Path(supplied).resolve()
    else:
        source = config["nativeTestSource"]
        repository_name(source["repository"])
        if not re.fullmatch(r"[0-9a-f]{40}", source["commit"]):
            raise ValueError("Native test source must be pinned to a full commit SHA.")
        cache = ROOT / ".work" / "native-source"
        root = cache / ("jellyfin-" + source["commit"])
        if not root.exists():
            cache.mkdir(parents=True, exist_ok=True)
            archive_path = cache / (source["commit"] + ".zip")
            url = f"https://codeload.github.com/{source['repository']}/zip/{source['commit']}"
            print("Downloading pinned Jellyfin native-test source.", flush=True)
            with urllib.request.urlopen(url, timeout=180) as response, archive_path.open("wb") as output:
                while chunk := response.read(1024 * 1024):
                    output.write(chunk)
            with zipfile.ZipFile(archive_path) as archive:
                for entry in archive.infolist():
                    target = (cache / entry.filename).resolve()
                    if not target.is_relative_to(cache.resolve()) or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                        raise ValueError("Unsafe entry in native-test source archive.")
                archive.extractall(cache)
    if not (root / "Jellyfin.Server.Implementations/Item/NextUpService.cs").is_file():
        raise ValueError("Native source path is not the expected Jellyfin 12.4 source tree.")
    return root


def build_plugins(config: dict, dotnet: str, source: Path, packages: str | None, no_restore: bool) -> None:
    environment = dict(os.environ)
    environment.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_GENERATE_ASPNET_CERTIFICATE="false",
                       DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="false")
    environment.setdefault("DOTNET_CLI_HOME", str(ROOT / ".work" / "dotnet-home"))
    for spec in config["plugins"]:
        directory = inside(ROOT, spec["directory"])
        meta = read_json(directory / "meta.json")
        common = ["-m:1", "-nr:false", "-p:UseSharedCompilation=false", "-p:ContinuousIntegrationBuild=true",
                  "-p:IncludeSourceRevisionInInformationalVersion=false", f"-p:Version={meta['version']}",
                  f"-p:AssemblyVersion={meta['version']}", f"-p:FileVersion={meta['version']}",
                  f"-p:JellyfinVersion={spec['jellyfinVersion']}"]
        if spec["nativeTests"]:
            common.append(f"-p:JellyfinSourceRoot={source}")
        def run(arguments: list[str]) -> None:
            print(f"{meta['name']}: dotnet {arguments[0]}", flush=True)
            subprocess.run([dotnet, *arguments, *common], cwd=directory, env=environment, check=True)
        if not no_restore:
            arguments = ["restore", spec["solution"], "--configfile", str(ROOT / "NuGet.Config"), "--disable-parallel"]
            if packages:
                arguments.extend(["--packages", str(Path(packages).resolve())])
            run(arguments)
        assets = read_json(inside(directory, spec["project"]).parent / "obj/project.assets.json")
        if f"Jellyfin.Controller/{spec['jellyfinVersion']}" not in assets["libraries"]:
            raise ValueError(f"{meta['name']}: assets do not match its declared Jellyfin ABI; restore first.")
        run(["build", spec["solution"], "--configuration", "Release", "--no-restore", "--nologo"])
        run(["test", spec["solution"], "--configuration", "Release", "--no-build", "--no-restore", "--nologo"])
        if spec.get("publish"):
            run(["publish", spec["project"], "--configuration", "Release", "--no-restore", "--nologo",
                 "-p:IncludeJellyfinRuntime=false", "--output", str(directory / "artifacts/catalog-publish")])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY"))
    parser.add_argument("--tag", default="local-validation")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    parser.add_argument("--previous-manifest", type=Path)
    parser.add_argument("--jellyfin-source")
    parser.add_argument("--packages")
    parser.add_argument("--no-restore", action="store_true")
    args = parser.parse_args()
    config = load_configuration()
    repository = repository_name(args.repository or config["githubRepository"])
    source = prepare_native_source(config, args.jellyfin_source)
    build_plugins(config, args.dotnet, source, args.packages, args.no_restore)
    previous = read_json(args.previous_manifest) if args.previous_manifest else []
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    timestamp = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    catalog, _ = create_catalog(ROOT, config, repository, args.tag, output, previous, timestamp)
    print(f"Validated {len(catalog)} catalog entries. Release assets: {output}")
    print(f"Repository URL after publishing: https://github.com/{repository}/releases/latest/download/manifest.json")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        print(f"Build stopped: {error}", file=sys.stderr)
        raise SystemExit(1)
