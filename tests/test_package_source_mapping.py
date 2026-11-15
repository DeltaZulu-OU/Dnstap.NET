import importlib.util
import pathlib
import tempfile
import unittest
import xml.etree.ElementTree as ET

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / ".github/scripts/configure-nuget-sources.py"
spec = importlib.util.spec_from_file_location("configure_nuget_sources", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class PackageSourceMappingTests(unittest.TestCase):
    def test_local_feed_has_exclusive_exact_mapping(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "NuGet.Config"
            module.configure(path, "local", "/tmp/validated-package")
            root = ET.parse(path).getroot()
            sources = root.find("packageSources")
            self.assertEqual(["clear", "add", "add"], [x.tag for x in sources])
            entries = [(x.get("key"), x.get("value")) for x in sources if x.tag == "add"]
            self.assertEqual([("local", "/tmp/validated-package"), ("nuget.org", "https://api.nuget.org/v3/index.json")], entries)
            mapping = root.find("packageSourceMapping")
            self.assertEqual(["Dnstap.NET"], [x.get("pattern") for x in mapping.find("packageSource[@key='local']")])
            self.assertEqual(["*"], [x.get("pattern") for x in mapping.find("packageSource[@key='nuget.org']")])
            module.configure(path, "local", "/tmp/validated-package")
            self.assertEqual(2, len(root.findall("packageSourceMapping/packageSource")))

    def test_github_mapping_and_preserved_config_sections(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "NuGet.Config"
            path.write_text("<configuration><packageSourceCredentials><github><add key='Username' value='test'/></github></packageSourceCredentials></configuration>")
            module.configure(path, "github", "https://nuget.pkg.github.com/DeltaZulu-OU/index.json")
            root = ET.parse(path).getroot()
            self.assertIsNotNone(root.find("packageSourceCredentials"))
            self.assertEqual("Dnstap.NET", root.find("packageSourceMapping/packageSource[@key='github']/package").get("pattern"))

    def test_rejects_unrecognized_source_name(self):
        with self.assertRaises(ValueError):
            module.configure("/tmp/unused.config", "nuget.org", "https://example.org")

if __name__ == "__main__":
    unittest.main()
