# Dnstap.NET — scope and implementation order

**Purpose:** A .NET DNS server or proxy can write dnstap logs to a file or existing collector. The same generated protobuf message can be serialized and parsed in C#.

**Deliverables:**

| Project | Responsibility |
|---|---|
| `src/Dnstap` | Vendored upstream `dnstap.proto`, Google.Protobuf C# types, Frame Streams file writer and duplex sender over a caller-owned `Stream`. Internal frame decoder for protocol checks. |
| `Tapper/` | Standalone CLI using **only the library's public API**; deterministic DNS fixtures, `file` and `tcp` commands, `--count` option, nonzero exit on local errors. |
| `tests/` | MSTest behavioral tests and independent file/socket interoperability verification, using Tapper where useful. |

**Protocol:** File: START → DATA* → STOP. Duplex: READY → ACCEPT → START → DATA* → STOP → FINISH. Protobuf content type: `protobuf:dnstap.Dnstap`. Validate proto2 required fields and frame sizes **before writing any DATA bytes**. Preserve packet bytes and metadata without parsing DNS packets. Serialize with Google.Protobuf; do not implement a second protobuf codec or message abstraction.

**Lifecycle:** Reject non-writable streams at construction. A pre-cancelled start may be retried without writing bytes; an incomplete I/O or protocol exchange faults the session. Duplex completion and disposal wait for FINISH with a finite deadline. No public receiver or background reader.

**Boundary:** The DNS host creates the file/socket, owns reconnection, queues, drops, security, TLS and operational policy. Dnstap.NET owns protobuf correctness and Frame Streams protocol. Tapper is a test driver, **not** a server, queue, or log-forwarder.

**Build in order:** (1) concise specifications and examples; (2) Red tests; (3) minimal Green implementation; (4) verify CI; (5) Refactor for correctness, clarity and less code, with tests rerun; (6) independently verify file and collector output before declaring interoperability. Keep `main` green. No PR; branch deletion is manual.

[Protocol rules](FRAMESTREAMS-PROTOCOL.md) · [TDD discipline](TDD.md) · [Interop gates](DNS-COLLECTOR-INTEROP.md)

[v1.1](ARCHITECTURE-v1.1.md) is historical; its listener, metrics, queues, NativeAOT, public message model and transport management are **not** requirements.
