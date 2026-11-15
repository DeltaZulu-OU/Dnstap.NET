#!/usr/bin/env python3
"""Strict, read-only checks for NuGet release artifacts and GitHub Release assets."""
import hashlib
import json
import pathlib
import sys

def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as source:
        for block in iter(lambda: source.read(1048576), b""):
            h.update(block)
    return h.hexdigest()

def verify_same(expected, observed):
    if not pathlib.Path(observed).is_file():
        raise ValueError(f"Expected published package not found: {observed}")
    a, b = digest(expected), digest(observed)
    if a != b:
        raise ValueError(f"Package bytes differ: expected {a}, observed {b}")
    return a

def release_asset_state(metadata, tag, filename):
    if metadata.get("tag_name") != tag:
        raise ValueError("GitHub Release tag does not match the requested tag")
    # Reject draft releases during retries rather than treating them as published.
    if metadata.get("draft"):
        raise ValueError("Existing GitHub Release is still a draft")
    assets = metadata.get("assets")
    if not isinstance(assets, list):
        raise ValueError("GitHub Release response has no assets array")
    matches = [asset for asset in assets if isinstance(asset, dict) and asset.get("name") == filename]
    if len(matches) > 1:
        raise ValueError("Multiple GitHub Release assets have the requested filename")
    return "present" if matches else "missing"

def main(args):
    if len(args) == 4 and args[1] == "compare":
        print(verify_same(args[2], args[3]))
    elif len(args) == 5 and args[1] == "asset-state":
        with open(args[2], encoding="utf-8") as source:
            metadata = json.load(source)
        print(release_asset_state(metadata, args[3], args[4]))
    else:
        raise SystemExit("Usage: release-artifact.py compare EXPECTED NUPKG | asset-state RELEASE_JSON TAG FILENAME")

if __name__ == "__main__":
    main(sys.argv)
