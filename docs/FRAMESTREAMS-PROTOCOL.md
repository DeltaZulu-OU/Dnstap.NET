# Frame Streams protocol

This is the single protocol reference for the **Dnstap.NET alpha**. The library emits `dnstap.Dnstap` protobuf records over internal Frame Streams framing; the public API is sender-only. File decoding exists in tests, not as a public reader.

Authority: [fstrm v0.6.1 control.h](https://github.com/farsightsec/fstrm/blob/v0.6.1/fstrm/control.h), [libfstrm writer](https://github.com/farsightsec/fstrm/blob/v0.6.1/fstrm/writer.c), and the vendored [dnstap.proto](../src/Dnstap/Schema/dnstap.proto).

## Wire format

All lengths, control IDs and field IDs are **unsigned 32-bit big-endian** integers.

| Frame | Layout |
|---|---|
| Data | `uint32 payloadLength (>0)` followed by protobuf payload bytes |
| Control | `uint32(0)`, `uint32 controlLength`, then control payload |
| Control payload | `uint32 controlType`, followed by zero or more fields |
| Control field | `uint32 fieldType`, `uint32 fieldLength`, then field bytes |

A zero first word indicates a control frame, never an empty data frame. Example: `DATA("abc") = 00000003 616263`; `STOP = 00000000 00000004 00000003`.

| Control | ID | Number of CONTENT_TYPE fields |
|---|---:|---:|
| ACCEPT | 1 | Any |
| START | 2 | 0 or 1 |
| STOP | 3 | 0 |
| READY | 4 | Any |
| FINISH | 5 | 0 |

CONTENT_TYPE is field ID **1**. Dnstap.NET sends `protobuf:dnstap.Dnstap`; compare these bytes exactly. A START without a content type and an ACCEPT without any content types are protocol-valid. A nonempty ACCEPT list must contain the exact dnstap content type; the sender then advertises it in START. Reject invalid field counts and unknown field IDs. In an active file session, unknown control *types* are ignored for forward compatibility, but not malformed frames.

## Session order

- **File (unidirectional):** START → zero or more DATA → STOP. No READY, ACCEPT or FINISH. A complete file must end at STOP; the test reader rejects truncation, invalid ordering and trailing bytes.
- **Duplex (connected stream):** sender READY → receiver ACCEPT → sender START → zero or more DATA → sender STOP → receiver FINISH. FINISH acknowledges shutdown, not individual DATA records.

The sender validates FINISH when completing, but cannot detect an early FINISH during DATA transmission because no background receiver is implemented. The underlying connection must already exist and be readable and writable in duplex mode.

## Limits and failure rules

| Constraint | Dnstap.NET behavior |
|---|---|
| Control payload | 4–512 bytes, excluding its 8-byte frame header |
| CONTENT_TYPE value | 0–256 bytes |
| Outgoing DATA | 1–96 KiB by default; caller may configure `maxDataLength` |
| Test-only file reader | 1 MiB receive maximum by default |
| Length handling | Validate before allocation or integer conversion; assemble fragmented reads |
| Protobuf records | Require a MESSAGE envelope to contain a nested message with a valid type; reject missing required fields and unknown required enum values before writing |
| Lifecycle | START once; serialize writes; STOP once on a successful session; no STOP after a failed DATA write |

A pre-cancelled `StartAsync` attempt may be retried without sending bytes. Cancelling a write while waiting for the writer lock leaves it usable; cancellation or I/O failure during an actual frame write faults it. Never retry reading FINISH after a failed shutdown. `CompleteAsync` explicitly finalizes. `DisposeAsync` finalizes healthy sessions and **propagates shutdown failures, including timeouts**, while releasing the stream only if `leaveOpen` is false. Faulted sessions do not attempt STOP.

Duplex reads, writes, flushes and lock waits have a configurable **30-second default timeout per operation**. Timeouts cancel pending underlying I/O when the stream honors cancellation, and stop awaiting I/O even if it does not. The latter does **not** guarantee the underlying operation has stopped; the caller owns that transport risk. File operations use the caller's cancellation token and do not have this duplex timeout.

## Requirement IDs

Test names retain the original IDs. `Sender` describes shipped functionality; `oracle` denotes test-only decoding.

| ID | Requirement | Scope |
|---|---|---|
| FS-001 | Big-endian, unsigned frame and field lengths | Codec / oracle |
| FS-002 | Control payload 4–512 bytes | Sender / oracle |
| FS-003 | CONTENT_TYPE value at most 256 bytes | Sender / oracle |
| FS-004 | Incoming DATA size checked against configured limit | Oracle |
| FS-005 | Outgoing DATA size checked before writing | Sender |
| FS-006 | Reject truncated declared lengths | Oracle |
| FS-007 | Enforce control field cardinality | Sender / oracle |
| FS-008 | Reject unknown control field IDs | Decoder |
| FS-009 | Preserve unknown control types for post-START policy | Oracle |
| FS-010 | Validate START content type when present | Oracle |
| FS-011 | START → DATA* → STOP, with strict EOF/trailing-byte checks | Sender / oracle |
| FS-012 | Duplex handshake and FINISH validation on completion | Sender |
| FS-013 | Handle fragmented frames without losing bytes | Sender / oracle |
| FS-014 | No duplicate START/STOP; idempotent resource cleanup | Sender |
| FS-015 | Bound duplex handshake, writes, flushes and FINISH reads | Sender |
| FS-016 | Honor caller cancellation and clean up incomplete operations | Sender / oracle |
| FS-017 | Borrow test frame views without copying | Oracle |
| FS-018 | Avoid repeated buffer compaction when reading | Oracle |
| FS-019 | Write caller-provided frame payload without copying it again | Sender |
| FS-020 | Size read buffers from validated lengths | Oracle |

## Verification

Unit tests cover wire fixtures, protocol states, boundaries and failure paths. File-reader and strict EOF/trailing-byte tests verify a **test-only oracle**, not a shipped receiving API; libfstrm's file reader may accept truncated captures that the oracle rejects. CI uses **Tapper** to exercise both file and TCP output against pinned `golang-dnstap` and DNS-collector v3.3.7. DNS-collector is permissive in some error cases; passing its ingestion test establishes compatibility, not complete protocol conformance. Keep the independent Go checks and unit specifications.
