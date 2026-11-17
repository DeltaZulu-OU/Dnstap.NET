#!/usr/bin/env bash
set -euo pipefail
[[ "${TRACE:-0}" != 1 ]] || set -x
usage() {
  echo 'Usage: check-query-zone.sh go|bind READER DOTNET OUTPUT_DIR'
  echo 'Paths must be absolute; runs as normal user. Set TRACE=1 for debugging.'
}
if [[ "${1:-}" == -h || "${1:-}" == --help ]]; then usage; exit 0; fi
[[ $# == 4 ]] || { usage >&2; exit 2; }
mode="$1"; reader="$2"; dotnet="$3"; output="$4"
[[ "$mode" == go || "$mode" == bind ]] || { usage >&2; exit 2; }
[[ "$reader" == /* && "$dotnet" == /* && "$output" == /* ]] || { echo 'Absolute paths required' >&2; exit 2; }
[[ -x "$reader" && -x "$dotnet" ]] || { echo 'Reader or dotnet not executable' >&2; exit 2; }
[[ "$(id -u)" != 0 ]] || { echo 'Run without root' >&2; exit 2; }
command -v python3 >/dev/null
if [[ "$mode" == bind ]]; then python3 -c 'import yaml'; fi
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
mkdir -p "$output"
(cd "$root" && "$dotnet" run --project src/Tapper/Tapper.csproj -c Release --no-build -- file --output "$output/zone.fstrm" --count 1 --query-zone) > "$output/tapper.stdout" 2> "$output/tapper.stderr"
if [[ "$mode" == go ]]; then
  "$reader" -j -r "$output/zone.fstrm" > "$output/decoded.jsonl" 2> "$output/reader.stderr"
  python3 - "$output/decoded.jsonl" <<'PY'
import json, pathlib, sys
records = [json.loads(x) for x in pathlib.Path(sys.argv[1]).read_text().splitlines()]
assert len(records) == 1, len(records)
d = records[0]
assert d['identity'] == 'tapper'
assert d['message']['type'] == 'CLIENT_QUERY'
assert d['message']['query_zone'].lower().rstrip('.') == 'example.com', d['message']
PY
else
  "$reader" -y "$output/zone.fstrm" > "$output/decoded.yaml" 2> "$output/reader.stderr"
  python3 - "$output/decoded.yaml" <<'PY'
import pathlib, sys, yaml
records = list(yaml.safe_load_all(pathlib.Path(sys.argv[1]).read_text()))
assert len(records) == 1, len(records)
d = records[0]
assert d['identity'] == 'tapper'
assert d['message']['type'] == 'CLIENT_QUERY'
assert d['message']['query_zone'].lower().rstrip('.') == 'example.com', d['message']
PY
fi
echo "FS-028 $mode query_zone verified" | tee "$output/result.txt"
