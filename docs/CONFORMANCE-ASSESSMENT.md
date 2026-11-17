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
| FS-021 libfstrm TCP receiver interoperability | v0.6.1 at pinned commit, explicit and implicit Tapper completion, Go reader, negative control and per-target evidence | [Verified main Protocol CI 44045840941](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44045840941) and [latest 44063220636](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44063220636) |
| FS-023 Unix-domain socket capture | Tapper Unix mode, explicit and disposal completion against pinned libfstrm capture and Go decode | [Verified main Protocol CI 44049137954](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44049137954) |
| FS-022 independent file readers | libfstrm dump, BIND YAML, and Knot DNS output; truncated-file observations; pinned Ubuntu 24.04 packages and versions | [Verified main Protocol CI 44048251219](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44048251219) and [latest 44063220636](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44063220636) |
| FS-024 Unbound receiver interoperability | Unbound 1.26.2 pinned release tarball, TCP and Unix socket, explicit and implicit completion, decoded record and identity checks | [Verified main Protocol CI 44056861487](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44056861487) and [latest 44063220636](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44063220636) |
| FS-025 duplex control flushing | A test-only duplex peer buffers writes until FlushAsync; verifies READY is flushed before ACCEPT and START is flushed before StartAsync completes | [Passed main 44057847816](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44057847816) |
| FS-026 public flush | MSTest checks file/duplex no-frame flush, state and disposal errors, lock-wait cancellation, duplex deadline, and failed or canceled flush | [Passed main 44059022032](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44059022032) |
| 0.2.0 immutable producer metadata factory | Instance factory copies raw identity/version once, leaves empty values omitted, and supports concurrent event creation; Tapper and package consumer exercise it | [Passed main 44060137100](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44060137100) |
| FS-027 separate query/response instants | Factory unit tests for dual instants, precision, conflicts, pre-epoch times, and unordered times; independently decode both Tapper timestamps in Go and response-side fields in BIND (BIND's YAML omits query-side fields on CLIENT_RESPONSE) | [Passed main 44061888101](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44061888101) |
| FS-028 query zone | MSTest validates wire-format name lengths, termination, pointers, text rejection and raw-byte fidelity; external Go and BIND decode checks | [Passed main 44063220636](https://github.com/DeltaZulu-OU/Dnstap.NET/actions/runs/44063220636) |
| Peer sends premature FINISH during active DATA | No background read loop | **Known sender limitation**; not detectable without adding asynchronous duplex receive functionality |
| File reader strict trailing data and EOF | FS-011 test oracle | Covered in tests; no public receiving API |

The listed CI runs verified the implementation and interoperability at the corresponding commits. They do not establish receiving support in Dnstap.NET (writer-only), arbitrary future third-party version compatibility, or strict malformed-frame rejection by permissive external readers.

## Acceptance policy

Do not expand the public API solely to satisfy test-only reader cases. Keep file and duplex sequencing distinct and preserve exact binary content-type matching. A duplex timeout guarantees bounded caller waiting, not termination of noncooperative stream I/O. Timeouts or I/O failure during a frame or shutdown are terminal for the session.

Independent interop with libfstrm v0.6.1, the Go dnstap implementation and DNS-collector remains required for emitted files and live TCP sessions; permissive readers are not sufficient proof of malformed-frame rejection. All new protocol tests must use independently authored wire fixtures where feasible.

## Deferred scope

A public reader, early-FINISH detection during active writes, and custom transport/options abstractions remain separate feature decisions. A public reader is deferred until a concrete consumer defines its receiving contract; the test-only reader remains an independent conformance oracle. Transport/options abstractions are deferred unless specific consumers require them. Typed convenience record construction is provided by `DnsLogEntry` and `DnstapRecordFactory`, without changing the Frame Streams implementation. Their absence is not a writer conformance failure.
