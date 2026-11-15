#!/usr/bin/env python3
"""Constrain NuGet package restoration to the intended source by exact ID."""
import pathlib
import sys
import xml.etree.ElementTree as ET

def configure(filename, internal_name, internal_url):
    if internal_name not in ("local", "github") or not internal_url:
        raise ValueError("Expected a named local or github package source")
    path = pathlib.Path(filename)
    if path.is_file():
        root = ET.parse(path).getroot()
        if root.tag != "configuration":
            raise ValueError("Expected a NuGet configuration")
    else:
        root = ET.Element("configuration")
    sources = root.find("packageSources")
    if sources is None:
        sources = ET.SubElement(root, "packageSources")
    for child in list(sources):
        sources.remove(child)
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key=internal_name, value=internal_url)
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    old_mapping = root.find("packageSourceMapping")
    if old_mapping is not None:
        root.remove(old_mapping)
    mapping = ET.SubElement(root, "packageSourceMapping")
    own = ET.SubElement(mapping, "packageSource", key=internal_name)
    ET.SubElement(own, "package", pattern="Dnstap.NET")
    public = ET.SubElement(mapping, "packageSource", key="nuget.org")
    ET.SubElement(public, "package", pattern="*")
    ET.indent(root)
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)

if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("Usage: configure-nuget-sources.py FILE local|github SOURCE_URL")
    configure(*sys.argv[1:])
