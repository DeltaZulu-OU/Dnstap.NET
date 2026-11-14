# Internal Frame Streams protocol specification

Status: **normative for the Dnstap.NET implementation**, revision 1 (14 November 2026).
Parent architecture: [Dnstap.NET Architecture Baseline v1.2](ARCHITECTURE-v1.2.md).

## Scope and authority

Dnstap.NET is one managed .NET 10 library for **emitting dnstap logs from a .NET DNS server or proxy**. Frame Streams framing and sender-side control negotiation are **internal** details; this document does not create a new Fstrm.NET library or a public generic Frame Streams API. The decoder and receiver behavior described here exist as protocol reference and for validating writer output; a public dnstap reader/listener is expressly **out of scope**. The wire format is specified by libfstrm's published `control.h`, with its `control.c`, `reader.c`, and `writer.c` as implementation references:

- [Frame Streams wire format and state rules (fstrm 0.6.1)](https://github.com/farsightsec/fstrm/blob/v0.6.1/fstrm/control.h)
- [C control encoding and validation](https://github.com/farsightsec/fstrm/blob/v0.6.1/fstrm/control.c)
- [C reader](https://github.com/farsightsec/fstrm/blob/v0.6.1/fstrm/reader.c)
- [C writer](https://github.com/farsightsec/fstrm/blob/v0.6.1/fstrm/writer.c)

For compatibility, the two sources must not be confused: wire-level compatibility is judged against the protocol; implementation-specific policy is stated explicitly below. C's control decoder currently rejects unknown control types, while the published protocol requires future unknown control types to be ignored **after START**. The .NET parser must preserve their numeric type; the active session decides whether to ignore them.

## 1. Framing

All integer lengths and control identifiers are unsigned 32-bit values in network (big-endian) byte order.

| Wire item | Encoding | Interpretation |
|---|---|---|
| Data frame | `uint32 length` + exactly `length` payload bytes | `length > 0` |
| Control frame | `uint32(0)` + `uint32 controlLength` + exactly `controlLength` payload bytes | Zero is the control escape |
| Control payload | `uint32 controlType` followed by zero or more fields | Minimum payload is 4 bytes |
| Control field | `uint32 fieldType` + `uint32 fieldLength` + field bytes | All declared lengths checked before slicing |

Zero-length *data* is not representable because zero is reserved for the control-frame escape. A data payload is opaque bytes. No dnstap protobuf parsing occurs in the Frame Streams layer.

Sample hex bytes (whitespace for readability):

```text
DATA("abc") = 00 00 00 03 61 62 63
STOP        = 00 00 00 00 00 00 00 04 00 00 00 03
START       = 00 00 00 00 00 00 00 22 00 00 00 02
              00 00 00 01 00 00 00 16
              70 72 6f 74 6f 62 75 66 3a 64 6e 73 74 61 70 2e 44 6e 73 74 61 70
```

START above contains the 22-byte binary content type `protobuf:dnstap.Dnstap`. The control payload length is 34 bytes (4 + 8 + 22); the 8-byte framing header is **not** included in that length.

## 2. Control types and fields

| Control name | Numeric type | Permitted number of CONTENT_TYPE fields |
|---|---:|---:|
| ACCEPT | 1 | 0 or more |
| START | 2 | 0 or 1 |
| STOP | 3 | 0 |
| READY | 4 | 0 or more |
| FINISH | 5 | 0 |

The only currently defined field is CONTENT_TYPE (numeric field type 1). Its value is an opaque byte string, possibly containing zero bytes, without NUL termination. Compare content types by **length and bytes**, not by normalized or case-insensitive text.

The encoder always advertises `protobuf:dnstap.Dnstap`. A received START with no CONTENT_TYPE is protocol-valid and is accepted in file mode under baseline v1.1. When the field is present, it must exactly equal the dnstap content type. START with multiple CONTENT_TYPE fields, and STOP or FINISH with any CONTENT_TYPE field, are invalid. Unrecognized field *types* are rejected.

A decoder accepts unknown numeric control *types* as a structural control message. A session rejects them before START and ignores them after START, as required for forward compatibility. This distinction avoids treating a decoding failure as permission to skip malformed fields.

## 3. State machines

### Unidirectional file stream

```text
CREATED -- START --> ACTIVE -- zero or more DATA --> ACTIVE -- STOP --> ENDED
```

Only START may open the stream, and only STOP may close it. READY, ACCEPT, FINISH, duplicate START, data before START, and any bytes after STOP are errors. EOF before STOP is an error; EOF at the end after STOP is expected. A control type not defined today is ignored only during ACTIVE. Closing a writer that successfully started writes STOP once; repeated disposal must not write another STOP. A writer disposed **before** START produces no Frame Streams bytes. Such an empty file is not a valid completed capture; it must not be presented as one. A writer whose START or DATA write failed must not emit STOP during cleanup because the protocol stream may be partial or corrupt. **Explicit `CompleteAsync` must fail with `InvalidOperationException` when START was never sent or the writer is faulted.** `DisposeAsync` remains idempotent and may silently clean up an unstarted/faulted writer without falsely reporting a completed file.

### Bidirectional socket stream

```text
Writer                          Reader
  | --- READY (offered) --------> |
  | <------ ACCEPT (chosen) ----- |
  | --- START (0/1 type) -------> |
  | --- DATA (zero or more) ----> |
  | --- STOP -------------------> |
  | <------ FINISH -------------- |
```

FINISH is a **shutdown response**, never part of startup. START must be sent exactly once. A socket session must retain the same input stream and framing state between negotiation and normal data reads, so it cannot lose bytes already received with START. The sender/receiver Frame Streams roles are independent of which party initiated the underlying TCP connection. Reversed socket initiation is deferred per baseline v1.1.

## 4. Validation and memory limits

| ID | Constraint | Expected handling |
|---|---|---|
| FS-001 | Headers, type IDs and field lengths use big-endian unsigned uint32 | Decode exactly; never sign-extend |
| FS-002 | Control payload length 4..512 bytes | Reject outside range **before** allocating |
| FS-003 | CONTENT_TYPE payload length 0..256 bytes | Reject >256 |
| FS-004 | Data payload length 1..configured receive maximum; default 1 MiB | Reject oversize unsigned values before casting or allocating |
| FS-005 | Outgoing data payload 1..configured send maximum; default 96 KiB | Reject invalid payload before writing any bytes |
| FS-006 | Frame declared lengths must match actual bytes consumed | Partial reads are assembled; truncated frame fails |
| FS-007 | Known controls enforce legal field cardinality | Reject invalid combinations |
| FS-008 | Unknown control field types are invalid | Reject rather than silently skipping |
| FS-009 | Unknown control frame types after START may be ignored | Preserve unknown numeric type in codec |
| FS-010 | START content type optional; if present it must match dnstap bytes | Accept omitted; reject mismatch |
| FS-011 | File state START → DATA* → STOP | Reject ordering errors, early EOF, trailing bytes |
| FS-012 | Bidirectional state READY → ACCEPT → START / STOP → FINISH | Reject invalid transitions; no early FINISH |
| FS-013 | Preserve frame boundaries across partial reads and coalesced writes | No data loss or read-ahead handover |
| FS-014 | One session owns transport and protocol state | Cleanup on errors, no duplicate START/STOP |
| FS-015 | Timeouts bound handshake, each write, and FINISH wait | Applied when socket sessions are introduced |
| FS-016 | Asynchronous file reads and writes honor cancellation | Cancel promptly and release owned resources |
| FS-017 | Parsing exposes a borrowed view without mandatory per-frame copying | Lifetime is explicitly tied to retained source buffers |
| FS-018 | File-reader buffering avoids per-frame compaction | Track consumed offsets; compact only when reading more input; release oversized rentals after large frames |
| FS-019 | File writer transmits the caller's payload without copying it | Serialize the length header, then write the original payload memory without an intermediate allocation; preserve ordering |
| FS-020 | File reader sizes rentals directly from validated frame lengths | Once length is known, allocate enough for the full frame, rather than repeatedly growing and copying |

Control max 512 bytes excludes the 8-byte outer header. The default 96 KiB send maximum is a Dnstap.NET compatibility policy, **not** a Frame Streams protocol requirement; individual file writers can configure another positive bound. It is inspired by [golang-dnstap's 96 KiB input `MaxPayloadSize`](https://github.com/dnstap/golang-dnstap/blob/ebb538e7d351a58861a8f348491828214a1d8db2/FrameStreamInput.go). The 1 MiB default receive limit follows [golang-framestream `DEFAULT_MAX_PAYLOAD_SIZE = 1048576`](https://github.com/farsightsec/golang-framestream/blob/b6862efda285882ccc371ca05e26db353877d26d/framestream.go). These are implementation defaults, not universal wire limits.

A file-mode reader copies each accepted frame payload once into caller-owned storage but must not repeatedly compact already-buffered frames. Once the length is available and validated, buffer growth must target the full frame size rather than double through intermediate rentals. An owned pooled read buffer is returned on exit, and an oversized rental is replaced with a normal-sized buffer after it is no longer needed. The file writer avoids copying the caller's data payload by writing the four-byte length header and payload separately. This requires **two WriteAsync calls per data frame**, which can mean two system calls on an unbuffered stream. Reevaluate this trade-off for the socket transport stage, including buffered or scatter/gather options, with measurements before adding complexity. Strict trailing-byte rejection is required by the published framing rules, while libfstrm 0.6.1's reader stops consuming input at STOP and does not inspect trailing bytes. A file ending with extra bytes is rejected by Dnstap.NET as stricter validation; that distinction is an interoperability-policy choice, not evidence that those bytes were validated by libfstrm. Treat clean EOF at a frame boundary as a **codec** result. Whether EOF is legal is a **session-state** decision; for example, EOF before STOP in file mode is illegal.

## 5. Test oracle and sequencing

The feature-branch tests in `tests/Dnstap.Tests/Protocol` are executable acceptance criteria; `main` must remain green. Tests are brought into `main` only alongside passing implementations. Initial tests use manually specified wire bytes rather than comparing two implementations of our own encoder and decoder; assertions are deliberately independent of production encoding logic. The frame parser reports **Done**, **NeedMoreData**, or **InvalidData** independently; a truncated prefix is not intrinsically malformed until EOF is established. Parser APIs must not force a per-frame allocation.

The existing file-mode codec and state tests cover FS-001..FS-011 and file-scoped FS-013/FS-014, with additional tests for FS-016 through FS-020. These decoder tests validate the internal framing foundation, not a planned public receiver API. The functional tests verify direct large-frame sizing and an incomplete-header buffer-shrink case; they do not quantify CPU copy volume, which requires a controlled benchmark. They have passed CI; absence of independent captured fixtures remains an acknowledged gap. Missing or untested requirements must not be described as covered. FS-012, socket-level FS-013 and FS-015 acquire tests when the TCP slice begins. File fixtures generated by `fstrm_dump`, `golang-dnstap`, and other independent implementations remain an explicit subsequent interoperability gate: internally constructed fixtures are **not** cross-language evidence.

Tapper and the public writer now emit actual protobuf records. CI tests file and duplex socket output against the pinned independent Go dnstap implementation. A DNS-collector-specific acceptance check is still separate; no public receiver, queue, NativeAOT or metrics API is planned.
