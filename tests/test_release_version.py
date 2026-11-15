import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / ".github/scripts/check-release-version.py"
spec = importlib.util.spec_from_file_location("release_version", SCRIPT)
release_version = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_version)

class ReleaseVersionTests(unittest.TestCase):
    def test_stable_outranks_prerelease(self):
        self.assertLess(release_version.compare("1.0.0-preview.1", "1.0.0"), 0)
        self.assertGreater(release_version.compare("1.0.0", "1.0.0-rc.100"), 0)

    def test_prerelease_numeric_order(self):
        self.assertLess(release_version.compare("1.0.0-beta.9", "1.0.0-beta.10"), 0)
        self.assertLess(release_version.compare("1.0.0-9", "1.0.0-alpha"), 0)
        self.assertLess(release_version.compare("1.0.0-beta", "1.0.0-beta.1"), 0)

    def test_core_and_equal_versions(self):
        self.assertLess(release_version.compare("0.9.9", "0.10.0"), 0)
        self.assertEqual(release_version.compare("0.1.0-preview.1", "0.1.0-preview.1"), 0)

    def test_invalid_versions_are_rejected(self):
        for value in ("1.0", "01.0.0", "1.0.0-beta.01", "v1.0.0", "1.0.0+build.1"):
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    release_version.parse(value)

if __name__ == "__main__":
    unittest.main()
