# Writer conformance gap assessment

Baseline: sender-only .NET 10 Dnstap.NET. Normative protocol behavior is checked against the Frame Streams specification as implemented in pinned libfstrm v0.6.1, plus the official dnstap protobuf schema. Reference implementations supplement wire fixtures but are not themselves normative specifications.

| Area | Existing evidence | Disposition |
|---|---|---|
| Big-endian 32-bit frame and control fields | FS-001 wire fixtures | Covered |
| Control payload 4–512 bytes; content type at most 256 bytes | FS-002/003/007/008 boundary and cardinality tests | Covered |
| Data length checks before allocation, including unsigned overflow | FS-004/005, duplex oversized-ACCEPT tests | Covered for shipped writer and test oracle |
| File-mode START → DATA* → STOP; exact wire bytes | FS-011/014/019 and test-only file reader | Covered |
| Duplex READY → ACCEPT → START → DATA* → STOP → FINISH | DuplexWriterTests and DuplexFailureMatrixTests | Covered at sender API |
| Exact binary dnstap content type | FS-010, DuplexWriterTests | Covered |
| Fragmented input / truncated control replies | FS-006/013, DuplexFailureMatrixTests | Covered at sender API |
| Unknown control type after file START | FS-009 test-only oracle | Covered for oracle; not shipped as a reader |
| Timeout, cancellation and faulted shutdown | FS-015/016, virtual-time and failure-matrix tests | Covered; additional late noncooperative FINISH-read regression added |
| FS-021 libfstrm TCP receiver interoperability | v0.6.1 at pinned commit, explicit and implicit Tapper completion, Go reader, negative control and per-target evidence | CI external interop matrix; pending verified runs |
| FS-022 independent file readers | libfstrm dump, BIND YAML, and Knot DNS output; truncated-file observations; pinned Ubuntu 24.04 packages and versions | CI file-reader interop matrix; pending verified runs |
| Peer sends premature FINISH during active DATA | No background read loop | **Known sender limitation**; not detectable without adding asynchronous duplex receive functionality |
| File reader strict trailing data and EOF | FS-011 test oracle | Covered in tests; no public receiving API |

## Acceptance policy

Do not expand the public API solely to satisfy test-only reader cases. Keep file and duplex sequencing distinct and preserve exact binary content-type matching. A duplex timeout guarantees bounded caller waiting, not termination of noncooperative stream I/O. Timeouts or I/O failure during a frame or shutdown are terminal for the session.

Independent interop with libfstrm v0.6.1, the Go dnstap implementation and DNS-collector remains required for emitted files and live TCP sessions; permissive readers are not sufficient proof of malformed-frame rejection. All new protocol tests must use independently authored wire fixtures where feasible.

## Deferred scope

A public reader, early-FINISH detection during active writes, and custom transport/options abstractions remain separate feature decisions. A public reader is deferred until a concrete consumer defines its receiving contract; the test-only reader remains an independent conformance oracle. Transport/options abstractions are deferred unless specific consumers require them. Typed convenience record construction is provided by `DnsLogEntry` and `DnstapRecordFactory`, without changing the Frame Streams implementation. Their absence is not a writer conformance failure.
