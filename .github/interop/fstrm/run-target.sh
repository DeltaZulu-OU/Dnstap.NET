#!/usr/bin/env bash
set -euo pipefail
if [[ "${TRACE:-0}" == "1" ]]; then set -x; fi

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"
readonly REPO_ROOT="$(cd -- "$SCRIPT_DIR/../../.." && pwd)"
readonly CONTENT_TYPE="protobuf:dnstap.Dnstap"
readonly COUNT=3
readonly FSTRM_VERSION="0.6.1"
readonly FSTRM_COMMIT="1d6e11d750dfa8d15419814dfe0aa86d80000741"
RECEIVER_PID=""

usage() {
    cat <<'HELP'
Usage: run-target.sh TARGET FSTRM_CAPTURE DNSTAP_READER DOTNET OUTPUT_DIR [TCP_PORT]
Targets: tcp-explicit, tcp-no-complete
Tool paths and OUTPUT_DIR should be absolute. Build Tapper in Release first.
Set TRACE=1 to print each shell command. No root privileges are needed.
HELP
}

fail() {
    printf '%s\n' "FS-021: $*" >&2
    return 1
}

cleanup_receiver() {
    if [[ -n "$RECEIVER_PID" ]] && kill -0 "$RECEIVER_PID" 2>/dev/null; then
        kill -TERM "$RECEIVER_PID" 2>/dev/null || true
        wait "$RECEIVER_PID" 2>/dev/null || true
    fi
}

require_tool() {
    local tool="$1"
    if [[ "$tool" == */* ]]; then
        [[ -x "$tool" ]] || fail "Required tool is not executable: $tool"
    else
        command -v "$tool" >/dev/null || fail "Required tool is missing: $tool"
    fi
}

verify_capture_cli() {
    local capture="$1" evidence="$2" status=0
    "$capture" --help > "$evidence/fstrm-help.txt" 2>&1 || status=$?
    [[ "$status" -le 1 ]] || fail "fstrm_capture --help returned $status"
    local flag
    for flag in --type --tcp --port --write; do
        grep -Fq -- "$flag" "$evidence/fstrm-help.txt" || fail "Missing v0.6.1 flag: $flag"
    done
    status=0
    "$capture" --version > "$evidence/versions.txt" 2>&1 || status=$?
    [[ "$status" -le 1 ]] || fail "fstrm_capture --version returned $status"
    grep -Fq "$FSTRM_VERSION" "$evidence/versions.txt" || fail "Unexpected fstrm_capture version"
    printf 'libfstrm source: v%s @ %s\n' "$FSTRM_VERSION" "$FSTRM_COMMIT" >> "$evidence/versions.txt"
}

wait_for_listener() {
    local port="$1" evidence="$2" attempt
    for ((attempt=0; attempt<100; attempt++)); do
        if ss -ltn | grep -Eq "127[.]0[.]0[.]1:${port}[[:space:]]"; then
            return 0
        fi
        kill -0 "$RECEIVER_PID" 2>/dev/null || fail "Receiver stopped before listening; see $evidence/fstrm.stderr"
        sleep 0.1
    done
    fail "Timed out waiting for libfstrm TCP listener on $port"
}

assert_records() {
    local evidence="$1" expected="$2"
    jq -s -e --argjson expected "$expected" -f "$SCRIPT_DIR/assert-go-dnstap.jq" "$evidence/decoded.jsonl"
}

main() {
    if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then usage; return 0; fi
    if [[ "$#" -lt 5 || "$#" -gt 6 ]]; then usage >&2; return 2; fi
    local target="$1" capture="$2" reader="$3" dotnet="$4" evidence="$5"
    local port="${6:-16061}"
    local -a completion_args=()
    case "$target" in
        tcp-explicit) ;;
        tcp-no-complete) completion_args+=(--no-complete) ;;
        *) fail "Unsupported target: $target"; return 2 ;;
    esac
    if [[ ! "$port" =~ ^[0-9]+$ ]] || ((port < 1 || port > 65535)); then
        fail "TCP port must be in 1..65535"
        return 2
    fi
    require_tool "$capture"
    require_tool "$reader"
    require_tool "$dotnet"
    require_tool jq
    require_tool ss
    mkdir -p "$evidence"
    verify_capture_cli "$capture" "$evidence"
    go version -m "$reader" >> "$evidence/versions.txt"
    printf 'FS-021 target: %s\n' "$target" >> "$evidence/versions.txt"

    "$capture" --type "$CONTENT_TYPE" --tcp 127.0.0.1 --port "$port" --write "$evidence/capture.fstrm" --debug --debug --debug \
        > "$evidence/fstrm.stdout" 2> "$evidence/fstrm.stderr" &
    RECEIVER_PID=$!
    trap cleanup_receiver EXIT
    wait_for_listener "$port" "$evidence"

    "$dotnet" run --project "$REPO_ROOT/src/Tapper/Tapper.csproj" -c Release --no-build -- \
        tcp --host 127.0.0.1 --port "$port" --count "$COUNT" "${completion_args[@]}" \
        > "$evidence/tapper.stdout" 2> "$evidence/tapper.stderr"
    # Tapper exiting successfully proves that STOP was acknowledged by FINISH.
    kill -TERM "$RECEIVER_PID"
    wait "$RECEIVER_PID"
    RECEIVER_PID=""
    trap - EXIT

    [[ -s "$evidence/capture.fstrm" ]] || fail "The libfstrm capture is empty"
    "$reader" -j -r "$evidence/capture.fstrm" > "$evidence/decoded.jsonl" 2> "$evidence/reader.stderr"
    assert_records "$evidence" "$COUNT" > "$evidence/assertion.log" 2>&1 || fail "Go dnstap assertions failed; see evidence"
    if assert_records "$evidence" "$((COUNT+1))" > "$evidence/negative-control.log" 2>&1; then
        fail "Negative control unexpectedly accepted the wrong record count"
    fi
    printf 'FS-021 negative control rejected expected count %d (actual %d)\n' "$((COUNT+1))" "$COUNT" >> "$evidence/negative-control.log"
    printf 'FS-021 %s: %d records, expected DNS IDs, and FINISH verified\n' "$target" "$COUNT"
}

main "$@"
