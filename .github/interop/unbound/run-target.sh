#!/usr/bin/env bash
set -euo pipefail
[[ "${TRACE:-0}" != 1 ]] || set -x
usage() {
  printf '%s\n' 'Usage: run-target.sh TARGET RECEIVER DOTNET OUTPUT_DIR [TCP_PORT]' 'Targets: tcp-explicit, tcp-no-complete, unix-explicit, unix-no-complete' 'Paths must be absolute. Set TRACE=1 for tracing; run as a normal user.'
}
if [[ "${1:-}" == --help || "${1:-}" == -h ]]; then usage; exit 0; fi
if (( $# < 4 || $# > 5 )); then usage >&2; exit 2; fi
target="$1"; receiver="$2"; dotnet="$3"; output="$4"; port="${5:-16071}"
case "$target" in tcp-explicit|tcp-no-complete|unix-explicit|unix-no-complete) ;; *) usage >&2; exit 2 ;; esac
[[ "$receiver" == /* && "$dotnet" == /* && "$output" == /* ]] || { echo 'Paths must be absolute' >&2; exit 2; }
[[ -x "$receiver" && -x "$dotnet" ]] || { echo 'Receiver or dotnet not executable' >&2; exit 2; }
[[ "$(id -u)" != 0 ]] || { echo 'Run without root' >&2; exit 2; }
command -v python3 >/dev/null
command -v ss >/dev/null
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
mkdir -p "$output"
socket="$output/unbound.sock"
rm -f "$socket"
pid=''
cleanup() {
  if [[ -n "$pid" ]]; then
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  rm -f "$socket"
}
trap cleanup EXIT
if [[ "$target" == tcp-* ]]; then
  "$receiver" -s "127.0.0.1@$port" -l > "$output/receiver.stdout" 2> "$output/receiver.stderr" &
else
  "$receiver" -u "$socket" -l > "$output/receiver.stdout" 2> "$output/receiver.stderr" &
fi
pid=$!
ready=0
for i in {1..100}; do
  if [[ "$target" == tcp-* ]]; then
    if ss -ltn | grep -qE ":$port[[:space:]]"; then ready=1; break; fi
  elif [[ -S "$socket" ]]; then ready=1; break; fi
  kill -0 "$pid" || { cat "$output/receiver.stderr" >&2; exit 1; }
  sleep 0.1
done
[[ "$ready" == 1 ]] || { echo 'Unbound receiver not ready' >&2; exit 1; }
args=()
if [[ "$target" == tcp-* ]]; then args=(tcp --host 127.0.0.1 --port "$port")
else args=(unix --path "$socket"); fi
args+=(--count 3)
if [[ "$target" == *-no-complete ]]; then args+=(--no-complete); fi
(cd "$root" && "$dotnet" run --project src/Tapper/Tapper.csproj -c Release --no-build -- "${args[@]}") > "$output/tapper.stdout" 2> "$output/tapper.stderr"
validate() {
  python3 - "$output/receiver.stdout" <<'PY'
import pathlib, re, sys
text = pathlib.Path(sys.argv[1]).read_text()
records = re.findall(r'^CLIENT_QUERY\s+192\.0\.2\.10\s+[^\n]*example\.com\.', text, re.M | re.I)
identity = re.findall(r'^identity:\s+tapper(?:\s|$)', text, re.M)
packets = re.findall(r'^query_message:', text, re.M)
sys.exit(0 if len(records) == len(identity) == len(packets) == 3 else 1)
PY
}
ok=0
for i in {1..100}; do
  if validate; then ok=1; break; fi
  kill -0 "$pid" || { cat "$output/receiver.stderr" >&2; exit 1; }
  sleep 0.1
done
if [[ "$ok" != 1 ]]; then
  echo 'FS-024: Unbound did not decode three expected records' >&2
  cat "$output/receiver.stdout" "$output/receiver.stderr" >&2
  exit 1
fi
sleep 1
validate || { echo 'FS-024: output changed after flush interval' >&2; exit 1; }
printf 'FS-024 Unbound %s: 3 records decoded\n' "$target" | tee "$output/result.txt"
