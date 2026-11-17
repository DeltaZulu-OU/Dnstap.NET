#!/usr/bin/env python3
"""FS-022: normalize each independent file reader's documented output."""
import json
import re
import sys
from pathlib import Path

EXPECTED_IDS = [0, 1, 2]
NAME = re.compile(r"(?m)^[;\s]*example\.com\.\s+IN\s+A\b", re.IGNORECASE)
ID = re.compile(r"\bid:\s*(\d+)\b", re.IGNORECASE)


def check(condition, description):
    if not condition:
        raise ValueError(f"FS-022: {description}")


def normalize_fstrm_dump(stdout, stderr, canonical):
    # libfstrm displays one escaped/quoted protobuf payload per DATA frame.
    quoted = [line for line in stdout.splitlines()
              if line.strip().startswith('"') and line.strip().endswith('"')]
    check(len(quoted) == len(canonical), "fstrm_dump DATA line count differs")
    check("FSTRM_CONTROL_START" in stderr, "fstrm_dump did not report START")
    check("FSTRM_CONTROL_STOP" in stderr, "fstrm_dump did not report STOP")
    check(stderr.count("Data frame (") == len(canonical),
          "fstrm_dump framing count differs")
    # The companion dump output is byte-compared and independently Go-decoded by the caller.
    return canonical


def normalize_bind(stdout, canonical):
    try:
        import yaml
    except ImportError as error:
        raise ValueError("FS-022: missing python3-yaml") from error

    docs = [doc for doc in yaml.safe_load_all(stdout) if doc is not None]
    check(len(docs) == len(canonical), "dnstap-read YAML document count differs")
    records = []
    for doc, expected in zip(docs, canonical, strict=True):
        check(isinstance(doc, dict), "dnstap-read emitted a non-map YAML document")
        message = doc.get("message")
        check(isinstance(message, dict), "dnstap-read omitted a message map")
        for field in ("type", "identity"):
            check(doc.get(field) == expected.get(field),
                  f"dnstap-read {field} differs from Go reader")
        for field in ("type", "socket_family", "socket_protocol",
                      "query_address", "response_address", "query_port",
                      "response_port"):
            check(message.get(field) == expected["message"].get(field),
                  f"dnstap-read message.{field} differs from Go reader")
        check("query_time" in message, "dnstap-read missing query timestamp")
        packet = message.get("query_message")
        check(isinstance(packet, str), "dnstap-read missing DNS packet text")
        check(bool(NAME.search(packet)), "dnstap-read QNAME/QTYPE incorrect")
        check(bool(ID.search(packet)), "dnstap-read missing DNS ID")
        records.append({"dns_id": int(ID.search(packet).group(1)),
                        "qname": "example.com.",
                        "identity": doc["identity"],
                        "message_type": message["type"],
                        "query_address": message["query_address"],
                        "response_port": message["response_port"]})
    check(sorted(record["dns_id"] for record in records) == EXPECTED_IDS,
          "dnstap-read DNS IDs missing or duplicated")
    return records


def normalize_kdig(stdout, canonical):
    # kdig -G prints the encapsulated DNS packets, not all dnstap metadata.
    lines = list(re.finditer(r"(?m)^;;[^\n]*\bid:\s*(\d+)\b", stdout))
    check(len(lines) == len(canonical), "kdig DNS header count differs")
    ids = []
    for index, header in enumerate(lines):
        end = lines[index + 1].start() if index + 1 < len(lines) else len(stdout)
        packet = stdout[header.start():end]
        ids.append(int(header.group(1)))
        check(bool(NAME.search(packet)), "kdig packet has the wrong QNAME/QTYPE")
    check(sorted(ids) == EXPECTED_IDS, "kdig DNS IDs are missing or duplicated")
    # Identity, event type, address and port are not printed by kdig.
    # Those fields must still pass the shared Go-reader assertions.
    return [{"dns_id": value, "qname": "example.com."} for value in ids]


def main():
    if len(sys.argv) != 5:
        raise ValueError("Usage: parse-reader-output.py TARGET STDOUT STDERR CANONICAL_JSONL")
    target, output_path, error_path, canonical_path = sys.argv[1:]
    stdout = Path(output_path).read_text(encoding="utf-8", errors="replace")
    stderr = Path(error_path).read_text(encoding="utf-8", errors="replace")
    canonical = [json.loads(line) for line in Path(canonical_path).read_text().splitlines()
                 if line.strip()]
    check(len(canonical) == 3, "the shared Go reader did not return three records")
    if target == "fstrm-dump":
        parsed = normalize_fstrm_dump(stdout, stderr, canonical)
    elif target == "bind-dnstap-read":
        parsed = normalize_bind(stdout, canonical)
    elif target == "knot-kdig":
        parsed = normalize_kdig(stdout, canonical)
    else:
        raise ValueError(f"Unknown FS-022 reader: {target}")
    for record in parsed:
        print(json.dumps(record, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, TypeError, AttributeError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
