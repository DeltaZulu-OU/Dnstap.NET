#!/usr/bin/env bash
set -euo pipefail
if [[ "${TRACE:-0}" == "1" ]]; then set -x; fi

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"
readonly REPO_ROOT="$(cd -- "$SCRIPT_DIR/../../.." && pwd)"
readonly RECORD_COUNT=3

usage() {
    cat <<'HELP'
Usage: run-file-reader.sh TARGET READER_TOOL GO_DNSTAP DOTNET OUTPUT_DIR
Targets: fstrm-dump, bind-dnstap-read, knot-kdig
Supply tool paths and OUTPUT_DIR as absolute paths. Tapper must be built in Release.
TRACE=1 enables tracing. This script runs without root privileges.
HELP
}

fail() { printf 'FS-022: %s\n' "$*" >&2; return 1; }

require_tool() {
    local tool="$1"
    if [[ "$tool" == */* ]]; then
        [[ -x "$tool" ]] || fail "Tool not executable: $tool"
    else
        command -v "$tool" >/dev/null || fail "Missing required tool: $tool"
    fi
}

run_reader() {
    local target="$1" reader="$2" input="$3" output="$4" error="$5"
    case "$target" in
        fstrm-dump)
            "$reader" "$input" > "$output" 2> "$error" ;;
        bind-dnstap-read)
            "$reader" -y "$input" > "$output" 2> "$error" ;;
        knot-kdig)
            "$reader" -G "$input" +qr > "$output" 2> "$error" ;;
    esac
}

main() {
    if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then usage; return 0; fi
    if [[ "$#" -ne 5 ]]; then usage >&2; return 2; fi
    local target="$1" reader="$2" go_reader="$3" dotnet="$4" evidence="$5"
    case "$target" in
        fstrm-dump|bind-dnstap-read|knot-kdig) ;;
        *) fail "Unknown reader target: $target"; return 2 ;;
    esac
    require_tool "$reader"
    require_tool "$go_reader"
    require_tool "$dotnet"
    require_tool jq
    require_tool python3
    require_tool truncate
    require_tool cmp
    mkdir -p "$evidence"

    printf 'FS-022 reader=%s\n' "$target" > "$evidence/versions.txt"
    if [[ "$target" == "fstrm-dump" ]]; then
        printf 'libfstrm v0.6.1 @ 1d6e11d750dfa8d15419814dfe0aa86d80000741\n' >> "$evidence/versions.txt"
    else
        dpkg-query -W -f='${binary:Package}=${Version}\n' bind9-dnsutils knot-dnsutils python3-yaml >> "$evidence/versions.txt"
        if [[ "$target" == "knot-kdig" ]]; then "$reader" -V >> "$evidence/versions.txt" 2>&1; else printf 'BIND source: v9.18.39 @ 737584125e6b078481e176e814970f560e115f1a\\n' >> "$evidence/versions.txt"; fi
    fi
    go version -m "$go_reader" >> "$evidence/versions.txt"

    "$dotnet" run --project "$REPO_ROOT/src/Tapper/Tapper.csproj" -c Release --no-build -- \
        file --output "$evidence/original.fstrm" --count "$RECORD_COUNT" \
        > "$evidence/tapper.stdout" 2> "$evidence/tapper.stderr"
    [[ -s "$evidence/original.fstrm" ]] || fail "Tapper did not produce a file"
    "$go_reader" -j -r "$evidence/original.fstrm" > "$evidence/canonical.jsonl" 2> "$evidence/go.stderr"
    jq -s -e --argjson expected "$RECORD_COUNT" -f "$SCRIPT_DIR/assert-go-dnstap.jq" \
        "$evidence/canonical.jsonl" > "$evidence/canonical-assert.log"

    if [[ "$target" == "fstrm-dump" ]]; then
        "$reader" "$evidence/original.fstrm" "$evidence/replayed.fstrm" \
            > "$evidence/reader.stdout" 2> "$evidence/reader.stderr"
        cmp "$evidence/original.fstrm" "$evidence/replayed.fstrm"
        "$go_reader" -j -r "$evidence/replayed.fstrm" > "$evidence/replayed.jsonl" 2> "$evidence/replayed.stderr"
        jq -s -e --argjson expected "$RECORD_COUNT" -f "$SCRIPT_DIR/assert-go-dnstap.jq" \
            "$evidence/replayed.jsonl" > "$evidence/reader-assert.log"
    else
        run_reader "$target" "$reader" "$evidence/original.fstrm" \
            "$evidence/reader.stdout" "$evidence/reader.stderr"
    fi
    python3 "$SCRIPT_DIR/parse-reader-output.py" "$target" "$evidence/reader.stdout" \
        "$evidence/reader.stderr" "$evidence/canonical.jsonl" \
        > "$evidence/normalized.jsonl"

    # Observe a truncated capture independently; permissive readers may accept it.
    cp "$evidence/original.fstrm" "$evidence/truncated.fstrm"
    truncate -s -6 "$evidence/truncated.fstrm"
    local status=0
    run_reader "$target" "$reader" "$evidence/truncated.fstrm" \
        "$evidence/truncated.stdout" "$evidence/truncated.stderr" || status=$?
    printf 'truncated reader exit status: %d\n' "$status" > "$evidence/truncated-status.txt"
    printf 'FS-022 %s: exact file checks passed; truncated-file behavior recorded (exit %d)\n' "$target" "$status"
}

main "$@"
