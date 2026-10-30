from __future__ import annotations

import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import build_repository as build
import release_repository as release


class RepositoryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="jellyfin-catalog-test-")
        self.root = Path(self.temporary.name)
        self.output = self.root / "dist"
        self.spec = {"directory": "Example", "solution": "Example.slnx", "project": "Plugin/Plugin.csproj",
                     "assembly": "Example.dll", "jellyfinVersion": "12.4.0", "nativeTests": True,
                     "archivePrefix": "Example", "changelog": "An update.\nPreserve newlines."}
        self.meta = {"guid": "1c550a63-a1f1-446a-8f69-b7fedc04c384", "name": "Example", "version": "1.0.0.0",
                     "targetAbi": "12.4.0.0", "description": "Test", "overview": "Test", "owner": "owner",
                     "category": "General", "autoUpdate": True, "status": "Active"}
        self.config = {"schemaVersion": 1, "githubRepository": "owner/plugins", "name": "Test Plugins", "plugins": [self.spec]}
        project = self.root / "Example/Plugin"
        project.mkdir(parents=True)
        (project / "Plugin.csproj").write_text("<Project />", encoding="utf-8")
        (project / "Plugin.cs").write_text("class Plugin {}", encoding="utf-8")
        (self.root / "Example/Example.slnx").write_text("<Solution />", encoding="utf-8")
        assembly = project / "bin/Release/net10.0/Example.dll"
        assembly.parent.mkdir(parents=True)
        assembly.write_bytes(b"fixture assembly")
        self.save_metadata()
        build.write_json(self.root / "repository.json", self.config)

    def tearDown(self):
        self.temporary.cleanup()

    def save_metadata(self):
        build.write_json(self.root / "Example/meta.json", self.meta)

    def package(self, previous=None, tag="v1"):
        return build.create_catalog(self.root, self.config, "owner/plugins", tag, self.output,
                                    previous or [], "2026-10-30T12:00:00Z")

    def test_configuration_matches_abi_and_enables_updates(self):
        self.assertEqual(build.load_configuration(self.root), self.config)

    def test_wrong_abi_is_rejected(self):
        self.meta["targetAbi"] = "12.0.0.0"
        self.save_metadata()
        with self.assertRaisesRegex(ValueError, "ABI"):
            build.load_configuration(self.root)

    def test_disabled_auto_update_is_rejected(self):
        self.meta["autoUpdate"] = False
        self.save_metadata()
        with self.assertRaisesRegex(ValueError, "automatic updates"):
            build.load_configuration(self.root)

    def test_duplicate_guids_are_rejected(self):
        other = dict(self.spec, directory="Other")
        (self.root / "Other/Plugin").mkdir(parents=True)
        (self.root / "Other/Plugin/Plugin.csproj").write_text("<Project/>")
        (self.root / "Other/Example.slnx").write_text("<Solution/>")
        build.write_json(self.root / "Other/meta.json", self.meta)
        self.config["plugins"].append(other)
        build.write_json(self.root / "repository.json", self.config)
        with self.assertRaisesRegex(ValueError, "GUID"):
            build.load_configuration(self.root)

    def test_path_traversal_is_rejected(self):
        with self.assertRaises(ValueError):
            build.inside(self.root, "../outside")

    def test_zip_contains_only_the_plugin_and_install_descriptor(self):
        catalog, summary = self.package()
        with zipfile.ZipFile(self.output / summary[0]["file"]) as archive:
            self.assertEqual(set(archive.namelist()), {"Example.dll", "meta.json"})
            meta = json.loads(archive.read("meta.json"))
            self.assertTrue(meta["autoUpdate"])
            self.assertEqual(meta["guid"], catalog[0]["guid"])
            self.assertEqual(meta["version"], catalog[0]["versions"][0]["version"])

    def test_jellyfin_md5_is_for_the_downloaded_zip(self):
        catalog, summary = self.package()
        content = (self.output / summary[0]["file"]).read_bytes()
        self.assertEqual(catalog[0]["versions"][0]["checksum"], hashlib.md5(content, usedforsecurity=False).hexdigest())
        self.assertEqual(len(catalog[0]["versions"][0]["checksum"]), 32)

    def test_zip_is_deterministic(self):
        _, summary = self.package()
        archive = self.output / summary[0]["file"]
        first = archive.read_bytes()
        self.package(tag="v2")
        self.assertEqual(first, archive.read_bytes())

    def test_old_versions_are_kept_and_sorted_newest_first(self):
        previous, _ = self.package()
        self.meta["version"] = "1.1.0.0"
        self.save_metadata()
        catalog, _ = self.package(previous, "v2")
        self.assertEqual([v["version"] for v in catalog[0]["versions"]], ["1.1.0.0", "1.0.0.0"])
        self.assertIn("/v1/", catalog[0]["versions"][1]["sourceUrl"])

    def test_unchanged_versions_reuse_the_existing_immutable_asset(self):
        previous, _ = self.package()
        catalog, summary = self.package(previous, "v2")
        self.assertTrue(summary[0]["reused"])
        self.assertEqual(catalog[0]["versions"], previous[0]["versions"])

    def test_changed_plugin_requires_a_version_bump(self):
        previous, _ = self.package()
        (self.root / "Example/Plugin/Plugin.cs").write_text("class Different {}")
        with self.assertRaisesRegex(ValueError, "bump its version"):
            self.package(previous, "v2")

    def test_test_and_readme_changes_do_not_republish_the_plugin(self):
        previous, _ = self.package()
        (self.root / "Example/README.md").write_text("Updated guide.")
        (self.root / "Example/Tests").mkdir()
        (self.root / "Example/Tests/ExtraTests.cs").write_text("// new tests")
        _, summary = self.package(previous, "v2")
        self.assertTrue(summary[0]["reused"])

    def test_changelog_newlines_are_preserved(self):
        catalog, _ = self.package()
        self.assertEqual(catalog[0]["versions"][0]["changelog"], self.spec["changelog"])

    def test_retired_plugins_are_not_silently_removed_from_catalog_history(self):
        previous, _ = self.package()
        retired = copy.deepcopy(previous[0])
        retired["guid"] = "71c99661-e4e5-4d87-9901-3758313cb625"
        previous.append(retired)
        catalog, _ = self.package(previous, "v2")
        self.assertEqual(len(catalog), 2)

    def test_malformed_history_is_rejected(self):
        previous, _ = self.package()
        previous[0]["versions"][0]["checksum"] = "not-md5"
        with self.assertRaises(ValueError):
            self.package(previous, "v2")

    def test_release_asset_validation_checks_actual_zip_bytes(self):
        _, summary = self.package()
        assets = release.release_assets("owner/plugins", "v1", self.output)
        self.assertIn(self.output / "manifest.json", assets)
        archive = self.output / summary[0]["file"]
        archive.write_bytes(b"corrupted")
        with self.assertRaisesRegex(ValueError, "checksum"):
            release.release_assets("owner/plugins", "v1", self.output)

    def test_reused_old_assets_are_not_uploaded_under_a_new_tag(self):
        previous, _ = self.package()
        self.package(previous, "v2")
        assets = release.release_assets("owner/plugins", "v2", self.output)
        self.assertFalse(any(path.suffix == ".zip" for path in assets))

    def test_tags_and_repository_names_cannot_be_shell_expressions(self):
        for repository in ("owner/repo;evil", "https://github.com/owner/repo", "../repo"):
            with self.assertRaises(ValueError):
                build.repository_name(repository)
        with self.assertRaises(ValueError):
            self.package(tag="v1;evil")

    def test_versions_use_numeric_order(self):
        self.assertGreater(build.version_key("1.10.0.0"), build.version_key("1.9.0.0"))
        with self.assertRaises(ValueError):
            build.version_key("v1.0")

    def test_private_repositories_are_rejected_before_publication(self):
        self.package()
        with mock.patch.object(release, "github_json", return_value={"private": True}), \
             mock.patch.object(release.subprocess, "run") as command:
            with self.assertRaisesRegex(ValueError, "public GitHub repository"):
                release.publish("owner/plugins", "v1", self.output, False)
            command.assert_not_called()

    def test_archive_entry_directory_traversal_is_rejected_on_all_platforms(self):
        for name in ("../plugin.dll", "sub/plugin.dll", "sub\\plugin.dll"):
            with self.assertRaises(ValueError):
                build.deterministic_zip(self.output / "unsafe.zip", {name: b"data"})


if __name__ == "__main__":
    unittest.main()
