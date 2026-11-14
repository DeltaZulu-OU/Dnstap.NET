#!/usr/bin/env bash
set -euo pipefail

root="$1"
collector="$2"
mkdir -p "$root/tap-watch"
sed "s|__ROOT__|$root|g" .github/interop/dns-collector.yml > "$root/collector.yml"
"$collector" -config "$root/collector.yml" -test-config > "$root/config-check.log" 2>&1
"$collector" -config "$root/collector.yml" > "$root/collector.log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true' EXIT

ready=0
for i in {1..100}; do
  if ss -ltn | grep -qE ':16054[[:space:]]'; then ready=1; break; fi
  kill -0 "$pid" || { cat "$root/collector.log"; exit 1; }
  sleep 0.1
done
[[ "$ready" -eq 1 ]] || { cat "$root/collector.log"; exit 1; }

dotnet run --project Tapper/Tapper.csproj -c Release --no-build -- tcp --host 127.0.0.1 --port 16054 --count 3 --no-complete
dotnet run --project Tapper/Tapper.csproj -c Release --no-build -- file --output "$root/tap-watch/capture.tmp" --count 3
mv "$root/tap-watch/capture.tmp" "$root/tap-watch/capture.fstrm"

check_records() {
  local output="$1"
  [[ -s "$output" ]] && jq -s -e '
    length == 3 and
    (map(.dns.id) | sort) == [0, 1, 2] and
    all(.[]; .dnstap.operation == "CLIENT_QUERY" and
      .dnstap.identity == "tapper" and
      .network."query-ip" == "192.0.2.10" and
      .network."response-port" == "53" and
      ((.dns.qname | rtrimstr(".")) == "example.com"))
  ' "$output" > /dev/null 2>&1
}

success=0
for i in {1..120}; do
  if check_records "$root/tcp.jsonl" && check_records "$root/file.jsonl"; then
    success=1
    break
  fi
  kill -0 "$pid" || { cat "$root/collector.log"; exit 1; }
  sleep 0.5
done
if [[ "$success" -ne 1 ]]; then
  echo "DNS-collector failed exact record assertions; see artifacts" >&2
  cat "$root/tcp.jsonl" "$root/file.jsonl" 2>/dev/null || true
  tail -100 "$root/collector.log"
  exit 1
fi

# Recheck after the logfile flush interval so late duplicates cannot satisfy an early poll.
sleep 2
if ! check_records "$root/tcp.jsonl" || ! check_records "$root/file.jsonl"; then
  echo "DNS-collector record count or identity changed after the flush interval" >&2
  exit 1
fi
if grep -Eiq 'framestream reader error|unable to decode|worker.*busy|buffer is full|unexpected control framestream' "$root/collector.log"; then
  echo "DNS-collector reported a protocol or queue error" >&2
  tail -100 "$root/collector.log"
  exit 1
fi
echo "DNS-collector accepted all 3 exact records in file and TCP modes"
