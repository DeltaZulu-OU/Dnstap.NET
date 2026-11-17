#!/usr/bin/env bash
set -euo pipefail
[[ "${TRACE:-0}" != 1 ]] || set -x
usage() {
  echo 'Usage: check-response-timestamps.sh go|bind READER DOTNET OUTPUT_DIR'
  echo 'Readers and output directory must use absolute paths; requires Python 3 and jq or PyYAML.'
  echo 'Run as a normal user; TRACE=1 enables command tracing.'
}
if [[ "${1:-}" == --help || "${1:-}" == -h ]]; then usage; exit 0; fi
[[ $# == 4 ]] || { usage >&2; exit 2; }
mode="$1"; reader="$2"; dotnet="$3"; output="$4"
[[ "$mode" == go || "$mode" == bind ]] || { usage >&2; exit 2; }
[[ "$reader" == /* && "$dotnet" == /* && "$output" == /* ]] || { echo 'Absolute paths required' >&2; exit 2; }
[[ -x "$reader" && -x "$dotnet" ]] || { echo 'Reader or dotnet not executable' >&2; exit 2; }
[[ "$(id -u)" != 0 ]] || { echo 'Run without root privileges' >&2; exit 2; }
command -v python3 >/dev/null
if [[ "$mode" == bind ]]; then python3 -c 'import yaml'; fi
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
mkdir -p "$output"
(cd "$root" && "$dotnet" run --project src/Tapper/Tapper.csproj -c Release --no-build -- file --output "$output/response.fstrm" --count 0 --response-timestamps) > "$output/tapper.stdout" 2> "$output/tapper.stderr"
if [[ "$mode" == go ]]; then
  "$reader" -j -r "$output/response.fstrm" > "$output/decoded.jsonl" 2> "$output/reader.stderr"
  python3 - "$output/decoded.jsonl" <<'PY'
import json, pathlib, sys
lines = pathlib.Path(sys.argv[1]).read_text().splitlines()
assert len(lines) == 1, f'Expected one response, got {len(lines)}'
d = json.loads(lines[0])
m = d['message']
assert d['identity'] == 'tapper'
assert m['type'] == 'CLIENT_RESPONSE'
assert m['query_time'] == '2023-11-14T22:13:20.1234567Z', m.get('query_time')
assert m['response_time'] == '2023-11-14T22:13:21.2345678Z', m.get('response_time')
assert 'example.com.' in m['query_message']
assert 'example.com.' in m['response_message']
PY
else
  "$reader" -y "$output/response.fstrm" > "$output/decoded.yaml" 2> "$output/reader.stderr"
  python3 - "$output/decoded.yaml" <<'PY'
import datetime, pathlib, sys, yaml
documents = list(yaml.safe_load_all(pathlib.Path(sys.argv[1]).read_text()))
assert len(documents) == 1, len(documents)
d = documents[0]
m = d['message']
assert d['type'] == 'MESSAGE' and d['identity'] == 'tapper'
assert m['type'] == 'CLIENT_RESPONSE'
# BIND's YAML display is direction-specific: for CLIENT_RESPONSE it exposes
# the response time and response packet, not the corresponding query fields.
assert 'query_time' not in m
assert isinstance(m['response_time'], datetime.datetime)
assert int(m['response_time'].timestamp()) == 1_700_000_001, m['response_time']
assert m['response_message_data']['QUESTION_SECTION'][0].startswith('example.com.')
PY
fi
echo "FS-027 both timestamps verified by $mode reader" | tee "$output/result.txt"
