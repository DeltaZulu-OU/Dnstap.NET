"""Verify the distributable archive before any publishing side effects."""
import os
import pathlib
import sys
import xml.etree.ElementTree as ET
import zipfile

if len(sys.argv) != 2:
    raise SystemExit("Usage: verify-nupkg.py /path/to/Dnstap.NET.VERSION.nupkg")

package_path = pathlib.Path(sys.argv[1])
props = ET.parse("Directory.Build.props").getroot()
versions = [e.text.strip() for e in props.iter("Version") if e.text and e.text.strip()]
if len(versions) != 1:
    raise SystemExit("Directory.Build.props must define exactly one Version")
version = versions[0]
if package_path.name != f"Dnstap.NET.{version}.nupkg":
    raise SystemExit(f"Unexpected package name: {package_path.name}")
with zipfile.ZipFile(package_path) as package:
    names = set(package.namelist())
    for name in ("lib/net10.0/Dnstap.dll", "PACKAGE_README.md", "Dnstap.NET.nuspec"):
        if name not in names:
            raise SystemExit(f"Missing NuGet package entry: {name}")
    nuspec = ET.fromstring(package.read("Dnstap.NET.nuspec"))

def localname(element):
    return element.tag.rsplit("}", 1)[-1]

def first(name):
    matches = [e for e in nuspec.iter() if localname(e) == name]
    if len(matches) != 1:
        raise SystemExit(f"Expected exactly one {name} in nuspec")
    return matches[0]

if first("id").text != "Dnstap.NET" or first("version").text != version:
    raise SystemExit("Unexpected NuGet package ID or version")
license_node = first("license")
if license_node.attrib.get("type") != "expression" or license_node.text != "GPL-3.0-only":
    raise SystemExit("NuGet license must be GPL-3.0-only")
if first("readme").text != "PACKAGE_README.md":
    raise SystemExit("NuGet package readme metadata is incorrect")
commit = os.environ.get("GITHUB_SHA")
if commit:
    repository = first("repository")
    if repository.attrib.get("commit") != commit or repository.attrib.get("type") != "git":
        raise SystemExit(f"NuGet repository commit must match tested HEAD {commit}")
dependencies = [e for e in nuspec.iter() if localname(e) == "dependency"]
protobuf = [e for e in dependencies if e.attrib.get("id") == "Google.Protobuf"]
if len(protobuf) != 1 or protobuf[0].attrib.get("version") not in ("3.36.2", "[3.36.2,)"):
    raise SystemExit("Google.Protobuf dependency must be the declared minimum 3.36.2")
if any(e.attrib.get("id") == "Grpc.Tools" for e in dependencies):
    raise SystemExit("Build-only Grpc.Tools leaked into package dependencies")
print(f"Verified Dnstap.NET {version} NuGet package")
