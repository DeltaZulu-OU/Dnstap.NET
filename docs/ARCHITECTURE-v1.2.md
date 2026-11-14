# Dnstap.NET — Architecture Baseline v1.2

Status: **current and deliberately narrow**, 14 November 2026. Supersedes [v1.1](ARCHITECTURE-v1.1.md). Version 1.1 is retained only as an architectural and research record; its additional planned features are no longer requirements.

## Purpose

**Allow a DNS server or DNS proxy implemented in .NET to emit standards-compliant dnstap log records. Nothing more.**

The host application supplies DNS wire-format packets and their already-known metadata. Dnstap.NET serializes the records using the upstream `dnstap.proto` schema and writes them using the required Frame Streams framing and sender-side session protocol. An independent dnstap reader or collector must be able to consume that output.

Dnstap.NET is a **format encoder and writer**, not a DNS server, DNS proxy, listener, telemetry pipeline, or log-forwarding service.

## In scope

| Concern | Requirement |
|---|---|
| Data format | Emit actual `dnstap.Dnstap` protobuf envelopes, preserving schema-defined fields |
| Schema | Pin and vendor upstream `dnstap.proto`; commit generated C# types; use Google.Protobuf |
| Caller inputs | Accept the schema's generated protobuf message type directly; caller populates its message type, packet bytes, timestamps, addresses, ports, DNS transport, optional identity and other schema-defined metadata |
| Public surface | Minimal `DnstapWriter`-style output API and schema-generated types; **no second public message/event model, mapping layer, or high-level messaging API** |
| Output | Write to a caller-provided `Stream`; file and duplex-stream operation are distinct protocol modes |
| File stream | START → DATA* → STOP |
| Duplex stream | Sender-side READY → ACCEPT → START → DATA* → STOP → FINISH, as required by Frame Streams |
| Data validation | Validate proto2 required fields, frame/control sizes, content-type compatibility, state transitions, truncation and write ordering |
| Concurrency | Keep frames from interleaving if callers write concurrently; no external queuing policy |
| Ownership | Explicit `leaveOpen` behavior; deterministic completion, failure, cancellation and cleanup |
| Quality | MSTest specifications, pinned dependency versions, GitHub Actions build/test and independent C/Go interoperability fixtures |

The intended API may be as small as `DnstapWriter.StartAsync`, `WriteAsync(dnstap.Dnstap)`, `CompleteAsync`, and `DisposeAsync`. This is a behavioral sketch, not a finalized type/signature contract. The .NET host provides its `FileStream`, `NetworkStream`, or another readable/writable stream where applicable; the library does not establish or maintain connections.

For socket output, bidirectional negotiation is mandatory when used by the chosen collector; a provided network stream does not imply unidirectional file-mode framing. Preserve the receiving stream state across the handshake.

## Explicitly out of scope

- DNS query processing, response generation, recursive resolution, proxying or packet parsing.
- Receiving dnstap events as a product capability: **no public `DnstapListener`, receiver service, socket accept loop, or public read API**. An internal file decoder may remain for test verification.
- A second public `DnstapMessage` model, custom event hierarchy, protobuf-to-domain-model mapping or schema drift mapping layer.
- Metrics, `ILogger`, health endpoints, logging dashboards, filtering, aggregation, redaction, persistent spool or operational telemetry.
- Managed delivery pipelines: bounded `Channel<T>`, drop-and-count policies, batching, retries, reconnection, fan-out, queue capacity, retention or draining deadlines.
- TCP/Unix socket creation, TLS certificate policy, authentication or connection pooling. A DNS host or its infrastructure supplies the stream and handles those concerns.
- NativeAOT publish tests or AOT/trimming acceptance requirements.
- Background services, CLI tooling, collector management or server lifecycle management.

The host decides whether and how to queue or drop events. An asynchronous write may take time or fail; it must not be misrepresented as a guaranteed nonblocking send or remote receipt. The DNS server/proxy is responsible for keeping any blocking I/O off latency-sensitive query paths.

## Implementation boundary

```text
.NET DNS server or proxy
    owns DNS packet processing and any logging queue/reconnect/transport setup
    |
    | populated generated dnstap.Dnstap message
    v
Dnstap.NET: protobuf validation/serialization + internal Frame Streams writer
    |
    | bytes on caller-provided Stream
    v
File or independently operated dnstap collector
```

The existing `FrameCodec`, `ControlCodec`, `FileFrameSession`, and `FileFrameWriter` are the internal file-mode foundation, not evidence of a completed public library. Production reader functionality is **not** a goal. Protocol limits and validation rules remain governed by [FRAMESTREAMS-PROTOCOL.md](FRAMESTREAMS-PROTOCOL.md).

## Required interoperability evidence

1. The writer emits a real dnstap protobuf record from a fixture containing known DNS wire bytes and metadata.
2. An independent reader, such as golang-dnstap or libfstrm tooling, successfully decodes the file-mode output; fields match expected values.
3. The writer, given a connected duplex stream, completes sender-side negotiation and output to an independent collector, with correct STOP/FINISH shutdown.
4. Malformed/unfinished protobuf messages are rejected before an output data-frame header is written, with no silent truncation or corruption.
5. CI runs these tests with pinned external tooling or recorded independent fixtures, while unit tests exercise size limits, partial reads, state and cancellation.

No claim of interoperability or dnstap protobuf compatibility follows from internal file-frame unit tests alone.

## Implementation sequence

1. **Keep** the tested internal file-mode framing implementation, using its reader only for tests and validation.
2. Vendor/pin `dnstap.proto`, generate C# protobuf types and test genuine message serialization and proto2 initialization.
3. Expose a single minimal writer API that writes properly serialized envelopes to a supplied stream; add independent file-mode interop.
4. Add only sender-side duplex Frame Streams negotiation needed to write to common dnstap collectors; add independent socket interop.
5. Stop when a .NET DNS server/proxy can reliably emit consumable dnstap logs. Do not introduce ancillary server or log-forwarder features.

## Historical evidence and decisions

[Baseline v1.1](ARCHITECTURE-v1.1.md) preserves the November 2026 research, performance experiments, and reasons for choosing Google.Protobuf and internal Frame Streams. Its proposals for a listener, separate public message abstraction, AOT gate, metrics, delivery channels, TLS or reconnection were superseded by this deliberately smaller scope. The existing repository license is not changed by this revision.
