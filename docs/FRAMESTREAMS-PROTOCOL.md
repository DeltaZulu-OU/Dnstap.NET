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

CONTENT_TYPE is field ID **1**. Dnstap.NET sends `protobuf:dnstap.Dnstap`; compare these bytes exactly. A START without a content type and an ACCEPT without any content types are protocol-valid. Reject invalid field counts and unknown field IDs. In an active file session, unknown control *types* are ignored for forward compatibility, but not malformed frames.

## Session order

- **File (unidirectional):** START → zero or more DATA → STOP. No READY, ACCEPT or FINISH. A complete file must end at STOP; the test reader rejects truncation, invalid ordering and trailing bytes.
- **Duplex (connected stream):** sender READY → receiver ACCEPT → sender START → zero or more DATA → sender STOP → receiver FINISH. FINISH acknowledges shutdown, not individual DATA records.

The sender cannot detect a prematurely transmitted FINISH until it next reads a control frame; no background receiver is implemented. The underlying connection must already exist and be readable and writable in duplex mode.

## Limits and failure rules

| Constraint | Dnstap.NET behavior |
|---|---|
| Control payload | 4–512 bytes, excluding its 8-byte frame header |
| CONTENT_TYPE value | 0–256 bytes |
| Outgoing DATA | 1–96 KiB by default; caller may configure `maxDataLength` |
| Test-only file reader | 1 MiB receive maximum by default |
| Length handling | Validate before allocation or integer conversion; assemble fragmented reads |
| Protobuf records | Validate required fields and defined required enum values before writing |
| Lifecycle | START once; serialize writes; STOP once on a successful session; no STOP after a failed DATA write |

A pre-cancelled `StartAsync` attempt may be retried without sending bytes. Once an I/O or protocol exchange fails after starting, treat the writer as faulted; in particular, never retry reading FINISH after a failed shutdown. `CompleteAsync` explicitly finalizes. `DisposeAsync` finalizes healthy sessions and releases resources according to `leaveOpen`.

Duplex reads, writes, flushes and lock waits have a configurable **30-second default timeout per operation**. A timeout prevents the caller waiting forever, but cannot guarantee that a custom stream which ignores cancellation has physically stopped its pending I/O. File operations use the caller's cancellation token and do not have this duplex timeout.

## Verification

Unit tests cover wire fixtures, protocol states, boundaries and failure paths. CI uses **Tapper** to exercise both file and TCP output against pinned `golang-dnstap` and DNS-collector v3.3.7. DNS-collector is permissive in some error cases; passing its ingestion test establishes compatibility, not complete protocol conformance. Keep the independent Go checks and unit specifications.
