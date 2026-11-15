import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / ".github/scripts/release-artifact.py"
spec = importlib.util.spec_from_file_location("release_artifact", SCRIPT)
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)

class ReleaseArtifactTests(unittest.TestCase):
    def test_matching_and_conflicting_packages(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            candidate, published = root / "candidate.nupkg", root / "published.nupkg"
            candidate.write_bytes(b"validated package bytes")
            published.write_bytes(b"validated package bytes")
            self.assertEqual(release.digest(candidate), release.verify_same(candidate, published))
            published.write_bytes(b"conflicting package bytes")
            with self.assertRaisesRegex(ValueError, "differ"):
                release.verify_same(candidate, published)
            published.unlink()
            with self.assertRaisesRegex(ValueError, "not found"):
                release.verify_same(candidate, published)

    def test_release_asset_states(self):
        release_json = {"tag_name": "v0.1.0-preview.1", "draft": False, "assets": []}
        tag, filename = "v0.1.0-preview.1", "Dnstap.NET.0.1.0-preview.1.nupkg"
        self.assertEqual("missing", release.release_asset_state(release_json, tag, filename))
        release_json["assets"] = [{"name": filename}]
        self.assertEqual("present", release.release_asset_state(release_json, tag, filename))
        with self.assertRaisesRegex(ValueError, "tag"):
            release.release_asset_state(release_json, "v9.9.9", filename)
        release_json["assets"].append({"name": filename})
        with self.assertRaisesRegex(ValueError, "Multiple"):
            release.release_asset_state(release_json, tag, filename)
        release_json["draft"] = True
        with self.assertRaisesRegex(ValueError, "draft"):
            release.release_asset_state(release_json, tag, filename)

    def test_invalid_release_metadata(self):
        with self.assertRaisesRegex(ValueError, "assets"):
            release.release_asset_state({"tag_name": "v1.0.0"}, "v1.0.0", "x.nupkg")

if __name__ == "__main__":
    unittest.main()
