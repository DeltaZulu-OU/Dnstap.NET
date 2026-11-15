#!/usr/bin/env python3
"""Validate a proposed SemVer release against all existing v-prefixed Git tags.

A same-version tag is allowed only when it points to HEAD, to support recovery
after a package push has succeeded but GitHub Release creation has failed.
"""
import re
import subprocess
import sys
from functools import cmp_to_key

SEMVER = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
)

def parse(value):
    match = SEMVER.fullmatch(value)
    if not match:
        raise ValueError(f"Invalid SemVer release version: {value!r}")
    numeric = tuple(map(int, match.group(1, 2, 3)))
    pre = match.group(4)
    ids = () if pre is None else tuple(pre.split("."))
    if any(identifier.isdecimal() and len(identifier) > 1 and identifier[0] == "0" for identifier in ids):
        raise ValueError(f"Numeric prerelease identifier has leading zeros: {value!r}")
    return numeric, ids, pre is None

def compare(left, right):
    a, b = parse(left), parse(right)
    if a[0] != b[0]:
        return (a[0] > b[0]) - (a[0] < b[0])
    if a[2] != b[2]:
        return 1 if a[2] else -1
    for x, y in zip(a[1], b[1]):
        if x == y:
            continue
        nx, ny = x.isdecimal(), y.isdecimal()
        if nx != ny:
            return -1 if nx else 1
        vx, vy = (int(x), int(y)) if nx else (x, y)
        return (vx > vy) - (vx < vy)
    return (len(a[1]) > len(b[1])) - (len(a[1]) < len(b[1]))

def git(*args):
    return subprocess.check_output(["git", *args], text=True).strip()

def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: check-release-version.py VERSION")
    proposed = sys.argv[1]
    parse(proposed)
    tag = "v" + proposed
    all_tags = git("tag", "--list", "v*").splitlines()
    versions = []
    for existing in all_tags:
        try:
            parse(existing[1:])
        except ValueError as exc:
            # Do not silently ignore malformed release-looking tags.
            if re.match(r"v[0-9]", existing):
                raise SystemExit(str(exc))
            continue
        versions.append(existing[1:])

    same_tag = proposed in versions
    if same_tag:
        tagged_sha = git("rev-list", "-n", "1", tag)
        head_sha = git("rev-parse", "HEAD")
        if tagged_sha != head_sha:
            raise SystemExit(f"Release tag {tag} exists on {tagged_sha}, not HEAD {head_sha}.")
    earlier = [v for v in versions if v != proposed]
    if earlier:
        highest = sorted(earlier, key=cmp_to_key(compare))[-1]
        if compare(proposed, highest) <= 0:
            raise SystemExit(f"Version {proposed} must move forward from {highest}.")
    print(f"Release {tag} validated; existing tag on HEAD: {same_tag}.")

if __name__ == "__main__":
    main()
