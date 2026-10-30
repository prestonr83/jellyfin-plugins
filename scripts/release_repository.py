#!/usr/bin/env python3
"""Preserve catalog history and publish complete GitHub Releases without committing generated manifests."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

from build_repository import ROOT, read_json, repository_name, validate_catalog, write_json


def github_json(path: str, *, method: str = "GET", data: dict | bytes | None = None):
    headers = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28",
               "User-Agent": "jellyfin-plugin-catalog-release"}
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = "Bearer " + token
    body = None
    if data is not None:
        body = data if isinstance(data, bytes) else json.dumps(data).encode("utf-8")
        headers["Content-Type"] = "application/octet-stream" if isinstance(data, bytes) else "application/json"
    url = path if path.startswith("https://uploads.github.com/") else "https://api.github.com/" + path.lstrip("/")
    request = urllib.request.Request(url, headers=headers, method=method, data=body)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            if response.status == 204:
                return None
            return json.load(response)
    except urllib.error.HTTPError as error:
        if error.code == 404 and method == "GET":
            return None
        raise


def fetch_previous(repository: str, destination: Path) -> None:
    repository_name(repository)
    release = github_json(f"repos/{repository}/releases/latest")
    if release is None:
        write_json(destination, [])
        print("No published catalog yet; preparing the first release.")
        return
    asset = next((asset for asset in release["assets"] if asset["name"] == "manifest.json"), None)
    if asset is None:
        raise ValueError("The latest published release has no manifest.json. Repair it before creating a new catalog release.")
    url = asset["browser_download_url"]
    if not url.lower().startswith(f"https://github.com/{repository}/releases/download/".lower()):
        raise ValueError("Unexpected catalog download location.")
    # Catalog releases must be public. Do not forward GitHub credentials through download redirects.
    with urllib.request.urlopen(url, timeout=60) as response:
        content = response.read(8 * 1024 * 1024 + 1)
    if len(content) > 8 * 1024 * 1024:
        raise ValueError("Previous catalog exceeded the size limit.")
    catalog = json.loads(content)
    validate_catalog(catalog)
    write_json(destination, catalog)
    print(f"Retaining catalog history from {release['tag_name']}.")


def release_assets(repository: str, tag: str, output: Path) -> list[Path]:
    catalog = read_json(output / "manifest.json")
    validate_catalog(catalog)
    assets = {output / name for name in ("manifest.json", "release-summary.json", "release-notes.md")}
    prefix = f"https://github.com/{repository}/releases/download/{urllib.parse.quote(tag, safe='')}/"
    for plugin in catalog:
        for version in plugin["versions"]:
            if not version["sourceUrl"].startswith(prefix):
                continue
            filename = version["sourceUrl"][len(prefix):]
            if Path(filename).name != filename or not filename.endswith(".zip"):
                raise ValueError("Unexpected release asset filename.")
            archive = output / filename
            content = archive.read_bytes()
            if hashlib.md5(content, usedforsecurity=False).hexdigest() != version["checksum"]:
                raise ValueError(f"Catalog checksum does not match {filename}.")
            checksum_file = archive.with_suffix(archive.suffix + ".sha256")
            if checksum_file.read_text(encoding="ascii").split()[0] != hashlib.sha256(content).hexdigest():
                raise ValueError(f"SHA-256 sidecar does not match {filename}.")
            assets.update((archive, checksum_file))
    if not all(path.is_file() for path in assets):
        raise ValueError("One or more release assets are missing.")
    return sorted(assets)


def find_release(repository: str, tag: str) -> dict | None:
    release = github_json(f"repos/{repository}/releases/tags/{urllib.parse.quote(tag, safe='')}")
    if release is not None:
        return release
    # The tag endpoint finds published releases. Authenticated listings also
    # include drafts, which must be found before retrying an interrupted upload.
    page = 1
    while True:
        releases = github_json(f"repos/{repository}/releases?per_page=100&page={page}")
        if releases is None:
            raise ValueError("Could not list repository releases, including drafts.")
        match = next((release for release in releases if release["tag_name"] == tag
                      or (release.get("draft") and release.get("name") == tag
                          and release["tag_name"].startswith("untagged-"))), None)
        if match is not None:
            return match
        if len(releases) < 100:
            return None
        page += 1


def publish(repository: str, tag: str, output: Path, dry_run: bool) -> None:
    repository_name(repository)
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", tag):
        raise ValueError("Invalid release tag.")
    assets = release_assets(repository, tag, output)
    if dry_run:
        print(json.dumps({"repository": repository, "tag": tag, "assets": [path.name for path in assets]}, indent=2))
        return
    information = github_json(f"repos/{repository}")
    if information is None or information.get("private", True):
        raise ValueError("Jellyfin needs unauthenticated catalog downloads; publish from a public GitHub repository.")
    existing = find_release(repository, tag)
    if existing and not existing["draft"]:
        raise ValueError("This release is already public. Published catalogs/assets are immutable; use a new tag.")
    if not existing:
        if github_json(f"repos/{repository}/git/ref/tags/{urllib.parse.quote(tag, safe='')}") is None:
            raise ValueError("Release tag does not exist; push the tag before publishing.")
        existing = github_json(f"repos/{repository}/releases", method="POST", data={
            "tag_name": tag, "name": tag, "draft": True,
            "body": (output / "release-notes.md").read_text(encoding="utf-8"),
        })
    if existing is None or not existing.get("draft") or not isinstance(existing.get("id"), int):
        raise ValueError("GitHub did not return a draft release ID; no assets were uploaded or published.")
    release_path = f"repos/{repository}/releases/{existing['id']}"
    expected_names = {path.name for path in assets}
    # Reconcile only this draft's assets. Published releases never reach this path.
    for asset in existing.get("assets", []):
        if asset["name"] in expected_names:
            github_json(f"repos/{repository}/releases/assets/{asset['id']}", method="DELETE")
    for asset in assets:
        github_json(f"https://uploads.github.com/{release_path}/assets?name={urllib.parse.quote(asset.name, safe='')}",
                    method="POST", data=asset.read_bytes())
    uploaded = github_json(f"repos/{repository}/releases/{existing['id']}")
    if uploaded is None or not uploaded.get("draft") or not expected_names.issubset({asset["name"] for asset in uploaded["assets"]}):
        raise ValueError("Not all assets reached the draft release; it was not published.")
    # Publishing is last: Jellyfin keeps reading the previous complete catalog until this point.
    published = github_json(release_path, method="PATCH", data={"tag_name": tag, "draft": False, "make_latest": "true"})
    if published is None or published.get("draft", True) or published.get("tag_name") != tag:
        raise ValueError("GitHub did not confirm publication of the requested catalog release.")
    print(f"Published https://github.com/{repository}/releases/tag/{tag}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("previous", "publish"))
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY"))
    parser.add_argument("--tag")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    parser.add_argument("--destination", type=Path, default=ROOT / ".work/previous-manifest.json")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    repository = args.repository or read_json(ROOT / "repository.json")["githubRepository"]
    if args.command == "previous":
        fetch_previous(repository, args.destination)
    else:
        if not args.tag:
            parser.error("publish requires --tag")
        publish(repository, args.tag, args.output.resolve(), args.dry_run)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError, OSError) as error:
        print(f"Release stopped: {error}", file=sys.stderr)
        raise SystemExit(1)
