# Dnstap.NET — Architecture Baseline v1.1

> **Historical/superseded.** The current, narrower [Architecture Baseline v1.2](ARCHITECTURE-v1.2.md) limits Dnstap.NET to **emitting dnstap logs from .NET DNS servers or proxies**. Proposed receivers, public message abstractions, metrics, queues, reconnection, and NativeAOT requirements in this document no longer govern implementation.

We will implement Dnstap.NET as a single, fully managed .NET library, with Frame Streams encapsulated internally. The public API will expose dnstap messaging, sending, receiving, and configuration without exposing framing or handshake primitives. This establishes the architecture for implementation, and it does not imply that the components have already been developed or tested.

Version 1.1 incorporates a review of the C, Go, and CoreDNS implementations and a set of validation tests run on 2026-11-14. Section 8 records the evidence, and section 9 lists the decisions that remain open.

## 1. Architectural decisions

| Area | Decision |
| --- | --- |
| Library | Dnstap |
| Target framework | .NET 10 |
| Language | C# |
| Serialization | Google.Protobuf, with code generated from a vendored dnstap.proto and committed |
| Frame Streams | Internal implementation |
| Native dependencies | None |
| Transport | Standard .NET Stream, Socket, and TLS APIs |
| Async delivery | Bounded Channel<T> |
| Back-pressure default | Drop and count; blocking is opt-in |
| Memory management | Standard .NET pooling when justified |
| Public API | Dnstap-specific; serializer types stay internal |
| DNS packet parsing | Outside library scope |
| AOT and trimming | IsAotCompatible, with a NativeAOT publish smoke test in CI |
| Observability | System.Diagnostics.Metrics counters and ILogger abstractions |
| Test framework | MSTest |
| Licence | Open; see section 9 |
| Primary correctness criterion | Interoperability with independent implementations |

We will not recreate Fstrm.NET under another name. The internal protocol implementation will contain only the functionality necessary to support dnstap.

Version 1.0 named protobuf-net as the serializer. Google.Protobuf replaces it because it passed the NativeAOT and round-trip tests in section 8, whereas protobuf-net's compile-time serializers are still an experimental preview.

## 2. Component architecture

```text
Public API              DnstapClient · DnstapListener · DnstapOptions
Sending                 Bounded queue · Writer loop · Reconnect
Receiving               Accept loop · Handshake tasks · Reader loop · Message stream
Dnstap serialization    Public message model · Mapping · Google.Protobuf (internal)
Internal Frame Streams  Session · Negotiation · Frame codec · Control codec · FrameReader · FrameWriter
Diagnostics             Meter counters · ILogger
Transport               TCP · TLS · Unix domain socket · File/Stream
```

The sending and receiving paths share the same protocol codecs but have distinct lifecycle responsibilities. In v1, `DnstapClient` sends and dials, and `DnstapListener` receives and listens; section 9 covers the reversed topology.

## 3. Repository layout

```text
Dnstap.NET/
├── src/
│   └── Dnstap/
│       ├── Dnstap.csproj
│       ├── DnstapClient.cs
│       ├── DnstapListener.cs
│       ├── DnstapOptions.cs
│       ├── Messages/
│       │   ├── DnstapMessage.cs
│       │   └── DnstapSerializer.cs
│       ├── Protocol/
│       │   ├── FrameCodec.cs
│       │   ├── ControlCodec.cs
│       │   ├── FrameReader.cs
│       │   ├── FrameWriter.cs
│       │   └── FrameSession.cs
│       ├── Schema/
│       │   ├── dnstap.proto        (vendored, pinned to a dnstap.pb commit)
│       │   └── Dnstap.g.cs         (protoc output, committed)
│       ├── Transport/
│       │   └── TransportFactory.cs
│       ├── Diagnostics/
│       │   └── DnstapMetrics.cs
│       └── Internal/
│           └── WriterLoop.cs
├── tests/
│   ├── Dnstap.Tests/
│   └── Dnstap.InteropTests/
│       └── Fixtures/               (captures from independent implementations)
└── examples/
    └── Dnstap.Demo/
```

All types under `Protocol`, `Schema`, `Transport`, `Diagnostics`, and `Internal` will be internal unless a concrete requirement justifies public exposure. The public `DnstapMessage` maps to the generated types, so a later serializer change stays behind the API. A golden test fails when the vendored schema gains a field the public model leaves unmapped.

## 4. Protocol invariants

These are mandatory implementation rules.

| Invariant | Requirement |
| --- | --- |
| Startup | Bidirectional sessions use READY → ACCEPT → START |
| Shutdown | Bidirectional sessions use STOP → FINISH |
| File mode | START → DATA → STOP without negotiation |
| Content type | Always send protobuf:dnstap.Dnstap and compare it byte-for-byte. Accept a START without a content-type field, which the fstrm specification allows. Reject a START carrying more than one type |
| Framing | Network-byte-order 32-bit lengths |
| Reading | Handle fragmented reads and premature EOF |
| Allocation | Validate declared frame length before allocating |
| Limits | Control frames ≤ 512 bytes; content-type field ≤ 256 bytes; data frames ≤ a configured maximum, defaulting to 1 MiB on receive and 96 KiB on send |
| Integers | Treat lengths as unsigned 32-bit values and range-check them before any conversion or slicing |
| State | Reject data before START and invalid control transitions. After START, ignore unknown control frame types and reject unknown control field types |
| Required fields | Check `IsInitialized()` on every decoded payload; count and skip payloads that fail |
| Ownership | One session owns its transport and receive state |
| Concurrency | Only the writer loop touches the transport. Producers enqueue and never perform I/O |
| Timeouts | Bound the handshake, each write, and the FINISH wait |
| Accept isolation | Run each handshake in its own task, outside the accept loop |
| Cleanup | Close the transport on every failed handshake and on every reader exit |
| Reconnect | A reconnect builds a new session, and no protocol state survives from the old one |
| Trust | A TLS trust anchor that fails to load is a fatal configuration error. Chain validation uses a custom root trust store with no fallback to system roots |
| Receiver limits | Cap concurrent connections. A full receive channel stops reading, which pushes back through TCP |

`FrameSession` will own the protocol state from connection establishment through shutdown. It will not create a temporary negotiation reader and then discard that reader's buffered bytes. Each invariant requires at least one test before its corresponding implementation is considered complete; current coverage is reported by the TDD tests and CI. The rows added in v1.1 trace to defects listed in section 8.

## 5. Public API contract

The intended client API is asynchronous and lifecycle-aware.

| Method | Contract |
| --- | --- |
| ConnectAsync | Establish transport and complete negotiation within the handshake timeout |
| SendAsync | Accept a message into the delivery pipeline; it waits for space only when blocking is enabled, and otherwise behaves as TrySend |
| TrySend | Attempt immediate queue acceptance; return false and count the drop when the queue is full |
| FlushAsync | Wait until accepted messages are written and flushed to the transport, honouring cancellation |
| DisposeAsync | Stop acceptance, drain until the configured deadline, count the remainder as dropped, and close |

The listener will expose `IAsyncEnumerable<DnstapMessage>` or an equivalent asynchronous receive API. Each received message carries its connection metadata, including the remote endpoint and the negotiated content type.

We must distinguish three outcomes: accepted into the queue, written to the transport, and received by the remote application. Frame Streams does not acknowledge individual data frames, so the third outcome cannot be guaranteed by the library. The queue will implement the drop policy with `BoundedChannelFullMode.Wait` and `TryWrite`. The channel's own drop modes report success for discarded items, which would make the first outcome unreliable.

Callers will set the DNS transport (UDP, TCP, DoT, DoH, DoQ) explicitly on each message. The library will not infer it from the socket type, since that inference mislabels encrypted transports.

## 6. Implementation order

* Frame Streams codec and protocol state

    Implement data/control encoding, bounded frame reading, negotiation, and shutdown. Validate these with deterministic tests using `MemoryStream` and connected streams, and with file-mode fixtures captured from independent implementations.

* Dnstap protobuf model

    Vendor dnstap.proto, generate and commit the C# code, and implement the public model and its mapping. Test against independent protobuf fixtures and a golden test for schema drift.

* Transport and basic client/server

    Add TCP and Unix domain sockets. Build a demonstration that sends actual serialized dnstap messages and prints decoded events, and run socket interop tests against `fstrm_capture` and golang-dnstap's `dnstap` tool.

* Asynchronous delivery

    Introduce bounded channels, the drop-and-count default, opt-in blocking, cancellation, writer lifecycle, and graceful draining.

* Production reliability

    Add reconnect policies, TLS, failure handling, metrics, and cross-language interoperability tests against DNS servers (Unbound, BIND, Knot, CoreDNS) and collectors (DNS-collector, dnstap-receiver).

* Performance assessment

    Benchmark allocations, throughput, latency, and memory retention. Optimize only measured bottlenecks.

## 7. First implementation milestone

The first milestone will produce a minimal, protocol-correct vertical slice in two steps. The first step is file mode, where Dnstap.NET reads captures written by independent implementations and writes files that golang-dnstap's `dnstap -r` and `fstrm_dump` decode.

The second step is TCP sender → Frame Streams negotiation → protobuf-encoded dnstap message → TCP receiver → stdout. The same exchange will then run with `fstrm_capture` as the receiver.

Both sender and receiver will run as independent asynchronous tasks in the demonstration application. Every payload labelled `protobuf:dnstap.Dnstap` will be a serialized `dnstap.Dnstap` message, and the receiver will count and skip any payload that fails to decode.

The initial implementation will exclude queueing, reconnection, and TLS. Those features will follow after the synchronous protocol lifecycle and basic asynchronous I/O have passed tests.

Default limits now follow the reference implementations in section 4. Timeout values, queue capacity, and the drain deadline should be resolved through protocol requirements and tests rather than arbitrary constants, and section 9 lists the remaining decisions.

## 8. Validation evidence

| Claim | Result | Method |
| --- | --- | --- |
| Technitium DNS Server runs on .NET 10 | `DnsServerCore.csproj` targets `net10.0` at commit `c11fec6` (2026-11-04) | Source inspection |
| Channel drop modes hide drops | DropNewest, DropOldest, and DropWrite return true from `TryWrite` for every write; Wait returns false when full; the `itemDropped` callback counts drops | Test, .NET SDK 10.0.115 |
| Google.Protobuf keeps unknown fields | An unknown field 99 survived parse and re-serialisation byte-for-byte | Test, v36.2 runtime, code from current dnstap.proto |
| Google.Protobuf keeps unknown enum values | `socket_protocol = 99` parsed with `HasSocketProtocol` true and round-tripped unchanged | Same test |
| Google.Protobuf skips proto2 required checks | An envelope without `type` serialised and parsed; `IsInitialized()` returned false | Same test |
| Google.Protobuf runs under NativeAOT | Published and ran natively; one IL3050 warning in `FeatureSetDescriptor`, on a path the dnstap schema leaves unused | NativeAOT publish |
| protobuf-net under NativeAOT | The runtime model emits IL, which NativeAOT cannot run; compile-time serializers are `[Experimental]` (PBN9001) in 3.4.40 | protobuf-net `docs/aot.md` |
| Content-type and control-frame rules | START may omit the content type; more than one type in START or an unknown field type fails decoding; unknown control frame types after START are ignored | fstrm 0.6.1 `control.h`, `control.c` |
| Frame limits | Control ≤ 512 bytes; content type ≤ 256 bytes; reader default 1 MiB in fstrm and golang-framestream; golang-dnstap's input closes the connection on frames over 96 KiB | Source inspection |
| Defects behind the new invariants | CoreDNS listener write race and 4 s query-path stall; CoreDNS mTLS fail-open on a non-PEM CA file; golang-dnstap never reconnecting after a broken session; golang-framestream panic on 32-bit builds; golang-dnstap's sink never closing connections | Standalone repros |
| Existing .NET libraries | None found on NuGet; GitHub shows only zbalkan/DnstapLogger | Search, shallow |

Google.Protobuf was compiled from its v36.2 source because NuGet was unreachable during validation. The published package is built from the same source, so a repeat against the package is a confirmation step only.

## 9. Open decisions

| Decision | Options | Recommendation |
| --- | --- | --- |
| Licence | AGPL-3.0-or-later, as in the DeltaZulu estate, or a permissive licence | Permissive terms maximise adoption by DNS servers and collectors; AGPL keeps the estate consistent |
| Reversed topology | Support a sender that listens (CoreDNS `dnstap listen`) in v1 or later | Later, since it needs one queue per connected sink and names that separate role from direction, such as `DnstapSender` and `DnstapReceiver` with dial and listen options |
| Unidirectional sockets | Accept socket senders that skip READY and ACCEPT | Decide once the interop matrix shows whether any sender needs it |
| TLS counterparts | TLS sits outside the Frame Streams specification; CoreDNS and dnstap-receiver support it | Keep TLS in the production-reliability phase and test against both |
| Operational defaults | Queue capacity, timeouts, drain deadline | Set by tests in the asynchronous-delivery and reliability phases |
