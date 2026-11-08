import base64
import json
from pathlib import Path
import tempfile
import unittest

from dependency_inventory import generate, serialized

DIGEST = base64.b64encode(bytes(range(64))).decode("ascii")


class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        self.cache = self.root / "packages"
        (self.root / "global.json").write_text(
            '{"sdk":{"version":"10.0.403","rollForward":"disable"}}', encoding="utf-8"
        )
        (self.root / "Directory.Packages.props").write_text(
            '<Project><ItemGroup><PackageVersion Include="Example" Version="1.2.3"/>'
            '</ItemGroup></Project>', encoding="utf-8"
        )
        (self.root / "Praxis.slnx").write_text(
            '<Solution><Project Path="src/Example/Example.csproj"/></Solution>', encoding="utf-8"
        )
        project = self.root / "src/Example"
        project.mkdir(parents=True)
        (project / "Example.csproj").write_text(
            '<Project><ItemGroup><PackageReference Include="Example"/></ItemGroup></Project>',
            encoding="utf-8"
        )
        self.lock = project / "packages.lock.json"
        self.dependencies = {"Example": {
            "type": "Direct", "requested": "[1.2.3, )", "resolved": "1.2.3",
            "contentHash": DIGEST
        }}
        self.save_lock()
        nuspec = self.cache / "example/1.2.3/example.nuspec"
        nuspec.parent.mkdir(parents=True)
        nuspec.write_text(
            '<package><metadata><id>Example</id><license type="expression">MIT</license>'
            '</metadata></package>', encoding="utf-8"
        )

    def save_lock(self):
        self.lock.write_text(
            json.dumps({"version": 2, "dependencies": {"net10.0": self.dependencies}}),
            encoding="utf-8"
        )

    def test_inventory_includes_license_and_package_provenance(self):
        report = generate(self.root, self.cache)
        self.assertEqual("10.0.403", report["sdk"])
        self.assertEqual([{
            "package": "Example", "version": "1.2.3",
            "contentHash": DIGEST, "projects": ["src/Example"],
            "declaredLicense": {"kind": "expression", "value": "MIT"}
        }], report["packages"])
        self.assertTrue(serialized(report).endswith(b"\n"))

    def test_missing_lockfile_is_rejected(self):
        self.lock.unlink()
        with self.assertRaisesRegex(ValueError, "Missing package lockfile"):
            generate(self.root, self.cache)

    def test_central_version_drift_is_rejected(self):
        self.dependencies["Example"]["resolved"] = "1.2.4"
        self.save_lock()
        with self.assertRaisesRegex(ValueError, "Central version"):
            generate(self.root, self.cache)

    def test_corrupt_content_hash_is_rejected(self):
        self.dependencies["Example"]["contentHash"] = "not base64"
        self.save_lock()
        with self.assertRaisesRegex(ValueError, "Invalid package content hash"):
            generate(self.root, self.cache)

    def test_unlisted_project_dependency_is_rejected(self):
        self.dependencies["Extra"] = {"type": "Project"}
        self.save_lock()
        with self.assertRaisesRegex(ValueError, "Unexpected project dependency"):
            generate(self.root, self.cache)

    def test_missing_license_is_reported_without_fabricating_approval(self):
        nuspec = self.cache / "example/1.2.3/example.nuspec"
        nuspec.write_text("<package><metadata><id>Example</id></metadata></package>",
                          encoding="utf-8")
        report = generate(self.root, self.cache)
        self.assertEqual({"kind": "unknown", "value": ""},
                         report["packages"][0]["declaredLicense"])

    def test_legacy_license_url_is_not_promoted_to_expression(self):
        nuspec = self.cache / "example/1.2.3/example.nuspec"
        nuspec.write_text(
            '<package><metadata><licenseUrl>https://example.test/license</licenseUrl>'
            '</metadata></package>', encoding="utf-8"
        )
        self.assertEqual({"kind": "url", "value": "https://example.test/license"},
                         generate(self.root, self.cache)["packages"][0]["declaredLicense"])

    def test_conflicting_hashes_across_projects_are_rejected(self):
        second = self.root / "src/Other"
        second.mkdir(parents=True)
        (second / "Other.csproj").write_text(
            '<Project><ItemGroup><PackageReference Include="Example"/></ItemGroup></Project>',
            encoding="utf-8"
        )
        conflicting = {"Example": dict(self.dependencies["Example"], contentHash=
            base64.b64encode(bytes(reversed(range(64)))).decode("ascii"))}
        (second / "packages.lock.json").write_text(
            json.dumps({"version": 2, "dependencies": {"net10.0": conflicting}}),
            encoding="utf-8"
        )
        (self.root / "Praxis.slnx").write_text(
            '<Solution><Project Path="src/Example/Example.csproj"/>'
            '<Project Path="src/Other/Other.csproj"/></Solution>', encoding="utf-8"
        )
        with self.assertRaisesRegex(ValueError, "Conflicting hashes"):
            generate(self.root, self.cache)

    def test_unpinned_sdk_is_rejected(self):
        (self.root / "global.json").write_text(
            '{"sdk":{"version":"10.0.403","rollForward":"latestFeature"}}',
            encoding="utf-8"
        )
        with self.assertRaisesRegex(ValueError, "Exact SDK"):
            generate(self.root, self.cache)


if __name__ == "__main__":
    unittest.main()
