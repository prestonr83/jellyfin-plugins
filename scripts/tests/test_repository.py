from __future__ import annotations

import copy
from contextlib import chdir
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

    def test_provider_packages_only_explicit_runtime_dependencies(self):
        self.spec["runtimeAssemblies"] = ["Npgsql.dll", "Npgsql.EntityFrameworkCore.PostgreSQL.dll"]
        self.spec["publish"] = True
        directory = self.root / "Example/artifacts/catalog-publish"
        directory.mkdir(parents=True)
        for name in ("Example.dll", *self.spec["runtimeAssemblies"], "Microsoft.EntityFrameworkCore.dll"):
            (directory / name).write_bytes(b"fixture dependency")
        _, summary = self.package()
        with zipfile.ZipFile(self.output / summary[0]["file"]) as archive:
            self.assertEqual(set(archive.namelist()), {"Example.dll", "Npgsql.dll", "Npgsql.EntityFrameworkCore.PostgreSQL.dll", "meta.json"})

    def test_server_dependency_bundling_is_rejected(self):
        self.spec["runtimeAssemblies"] = ["Jellyfin.Controller.dll"]
        build.write_json(self.root / "repository.json", self.config)
        with self.assertRaisesRegex(ValueError, "Server/framework"):
            build.load_configuration(self.root)

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

    def test_git_text_line_endings_do_not_change_source_identity(self):
        source = self.root / "Example/Plugin/Plugin.cs"
        source.write_bytes(b"class Plugin {\n}\n")
        first = build.source_checksum(self.root, self.spec)
        source.write_bytes(b"class Plugin {\r\n}\r\n")
        self.assertEqual(first, build.source_checksum(self.root, self.spec))

    def test_relative_repository_roots_preserve_source_identity(self):
        (self.root / "Directory.Build.props").write_text("<Project />", encoding="utf-8")
        expected = build.source_checksum(self.root.resolve(), self.spec)
        with chdir(self.root.parent):
            self.assertEqual(expected, build.source_checksum(Path(self.root.name), self.spec))

    @unittest.skipUnless(sys.platform == "win32", "Windows short paths")
    def test_windows_short_paths_preserve_source_identity_and_published_assets(self):
        import ctypes

        get_short_path = ctypes.WinDLL("kernel32", use_last_error=True).GetShortPathNameW
        get_short_path.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint]
        get_short_path.restype = ctypes.c_uint
        buffer = ctypes.create_unicode_buffer(32768)
        length = get_short_path(str(self.root), buffer, len(buffer))
        if not length:
            raise ctypes.WinError(ctypes.get_last_error())
        self.assertLess(length, len(buffer))
        short_root = Path(buffer.value)
        long_root = self.root.resolve()
        if short_root == long_root:
            self.skipTest("The test directory has no short-path alias on this volume.")

        (self.root / "Directory.Build.props").write_text("<Project />", encoding="utf-8")
        previous, _ = build.create_catalog(long_root, self.config, "owner/plugins", "v1", self.output,
                                            [], "2026-10-30T12:00:00Z")
        catalog, summary = build.create_catalog(short_root, self.config, "owner/plugins", "v2", self.output,
                                                previous, "2026-10-30T12:00:00Z")
        self.assertEqual(build.source_checksum(long_root, self.spec), build.source_checksum(short_root, self.spec))
        self.assertEqual(catalog, previous)
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

    def _publication_api(self, *, existing=None, missing_asset=False, missing_tag=False, invalid_draft=False):
        files = {path.name: path.read_bytes() for path in release.release_assets("owner/plugins", "v1", self.output)}
        uploaded = []
        draft = {"id": 42, "tag_name": "untagged-server-placeholder", "name": "v1", "draft": True, "assets": []}

        def api(path, *, method="GET", data=None):
            if path == "repos/owner/plugins":
                return {"private": False}
            if path == "repos/owner/plugins/releases/tags/v1":
                return existing if existing and not existing["draft"] else None
            if path == "repos/owner/plugins/releases?per_page=100&page=1":
                # Even after creation, discovery may not expose the draft under its requested tag.
                return [existing] if existing else []
            if path == "repos/owner/plugins/git/ref/tags/v1":
                return None if missing_tag else {"ref": "refs/tags/v1"}
            if path == "repos/owner/plugins/releases" and method == "POST":
                self.assertEqual(data["tag_name"], "v1")
                self.assertTrue(data["draft"])
                return {"draft": True} if invalid_draft else draft
            if path == "repos/owner/plugins/releases/assets/7" and method == "DELETE":
                return None
            if path.startswith("https://uploads.github.com/repos/owner/plugins/releases/42/assets?"):
                self.assertEqual(method, "POST")
                name = release.urllib.parse.parse_qs(release.urllib.parse.urlsplit(path).query)["name"][0]
                self.assertEqual(data, files[name])
                uploaded.append({"name": name})
                return {"id": len(uploaded), "name": name}
            if path == "repos/owner/plugins/releases/42" and method == "GET":
                return dict(draft, assets=[] if missing_asset else uploaded)
            if path == "repos/owner/plugins/releases/42" and method == "PATCH":
                self.assertEqual({asset["name"] for asset in uploaded}, set(files))
                self.assertEqual(data, {"tag_name": "v1", "draft": False, "make_latest": "true"})
                return {"id": 42, "tag_name": "v1", "draft": False}
            self.fail(f"Unexpected API request: {method} {path}")
        return api

    def test_private_repositories_are_rejected_before_publication(self):
        self.package()
        with mock.patch.object(release, "github_json", return_value={"private": True}) as api:
            with self.assertRaisesRegex(ValueError, "public GitHub repository"):
                release.publish("owner/plugins", "v1", self.output, False)
        api.assert_called_once_with("repos/owner/plugins")

    def test_new_draft_is_published_using_only_its_creation_response_id(self):
        self.package()
        with mock.patch.object(release, "github_json", side_effect=self._publication_api()) as api:
            release.publish("owner/plugins", "v1", self.output, False)
        self.assertEqual(sum(call.args[0].endswith("/releases/tags/v1") for call in api.call_args_list), 1)
        api.assert_any_call("repos/owner/plugins/releases/42")
        self.assertEqual(api.call_args_list[-1].kwargs["method"], "PATCH")

    def test_interrupted_draft_upload_replaces_only_matching_draft_assets(self):
        self.package()
        draft = {"id": 42, "tag_name": "untagged-placeholder", "name": "v1", "draft": True,
                 "assets": [{"id": 7, "name": "manifest.json"}]}
        with mock.patch.object(release, "github_json", side_effect=self._publication_api(existing=draft)) as api:
            release.publish("owner/plugins", "v1", self.output, False)
        api.assert_any_call("repos/owner/plugins/releases/assets/7", method="DELETE")
        self.assertFalse(any(call.args[0] == "repos/owner/plugins/releases" and call.kwargs.get("method") == "POST"
                             for call in api.call_args_list))

    def test_missing_draft_asset_prevents_publication(self):
        self.package()
        with mock.patch.object(release, "github_json", side_effect=self._publication_api(missing_asset=True)) as api:
            with self.assertRaisesRegex(ValueError, "Not all assets"):
                release.publish("owner/plugins", "v1", self.output, False)
        self.assertFalse(any(call.kwargs.get("method") == "PATCH" for call in api.call_args_list))

    def test_public_release_is_never_overwritten(self):
        self.package()
        with mock.patch.object(release, "github_json", side_effect=self._publication_api(existing={"draft": False})) as api:
            with self.assertRaisesRegex(ValueError, "already public"):
                release.publish("owner/plugins", "v1", self.output, False)
        self.assertTrue(all(not call.kwargs.get("method") for call in api.call_args_list))

    def test_draft_lookup_checks_later_pages(self):
        first_page = [{"tag_name": f"other-{number}"} for number in range(100)]
        draft = {"id": 42, "tag_name": "v1", "draft": True}
        with mock.patch.object(release, "github_json", side_effect=[None, first_page, [draft]]) as api:
            self.assertEqual(release.find_release("owner/plugins", "v1"), draft)
        api.assert_any_call("repos/owner/plugins/releases?per_page=100&page=2")

    def test_invalid_creation_response_prevents_upload_and_publication(self):
        self.package()
        with mock.patch.object(release, "github_json", side_effect=self._publication_api(invalid_draft=True)) as api:
            with self.assertRaisesRegex(ValueError, "draft release ID"):
                release.publish("owner/plugins", "v1", self.output, False)
        self.assertFalse(any(call.args[0].startswith("https://uploads.") or call.kwargs.get("method") == "PATCH"
                             for call in api.call_args_list))

    def test_missing_tag_is_not_created_by_publication(self):
        self.package()
        with mock.patch.object(release, "github_json", side_effect=self._publication_api(missing_tag=True)) as api:
            with self.assertRaisesRegex(ValueError, "tag does not exist"):
                release.publish("owner/plugins", "v1", self.output, False)
        self.assertFalse(any(call.kwargs.get("method") for call in api.call_args_list))

    def test_empty_delete_response_is_accepted(self):
        import io

        response = io.BytesIO(b"")
        response.status = 204
        with mock.patch.object(release.urllib.request, "urlopen", return_value=response):
            self.assertIsNone(release.github_json("repos/owner/plugins/releases/assets/7", method="DELETE"))

    def test_upload_uses_exact_binary_body_and_github_upload_host(self):
        import io

        response = io.BytesIO(b'{"id": 7}')
        response.status = 201
        with mock.patch.object(release.urllib.request, "urlopen", return_value=response) as network:
            release.github_json("https://uploads.github.com/repos/owner/plugins/releases/42/assets?name=Example.zip",
                                method="POST", data=b"archive bytes")
        request = network.call_args.args[0]
        self.assertEqual(request.get_method(), "POST")
        self.assertEqual(request.data, b"archive bytes")
        self.assertEqual(request.get_header("Content-type"), "application/octet-stream")


    def test_archive_entry_directory_traversal_is_rejected_on_all_platforms(self):
        for name in ("../plugin.dll", "sub/plugin.dll", "sub\\plugin.dll"):
            with self.assertRaises(ValueError):
                build.deterministic_zip(self.output / "unsafe.zip", {name: b"data"})


if __name__ == "__main__":
    unittest.main()
