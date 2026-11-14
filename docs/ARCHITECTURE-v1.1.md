# Dnstap.NET — Architecture Baseline v1.1

Established: 14 November 2026. This is the governing design baseline for implementation, **not** a claim that components have been implemented or tested. Version 1.1 incorporates the review and validation evidence recorded by the project owner on that date. See [Frame Streams protocol specification](FRAMESTREAMS-PROTOCOL.md) for wire-level requirements and [TDD workflow](TDD.md) for development policy.

## 1. Architectural decisions

| Area | Decision |
|---|---|
| Library | Dnstap (single managed .NET library) |
| Target framework | .NET 10 |
| Language | C# |
| Serialization | Google.Protobuf, generated from vendored/pinned `dnstap.proto`, generated code committed |
| Frame Streams | Internal implementation; no separate Fstrm.NET project |
| Native dependencies | None |
| Transport | Standard .NET `Stream`, `Socket` and TLS APIs |
| Async delivery | Bounded `Channel<T>` |
| Backpressure default | Drop and count; opt-in blocking |
| Memory management | Standard .NET pooling when justified |
| Public API | Dnstap-specific; generated serializer types stay internal |
| DNS packet parsing | Outside library scope |
| AOT / trimming | `IsAotCompatible` and NativeAOT CI publish smoke test |
| Observability | `System.Diagnostics.Metrics` counters and `ILogger` |
| Tests | MSTest |
| Licence | Open; licensing options below. The repository currently contains AGPL-3.0. |
| Primary correctness criterion | Independent interoperability |

Google.Protobuf replaces protobuf-net from version 1.0 based on the reported NativeAOT and protobuf round-trip tests. Prior Fstrm.NET lessons may be reused selectively; its types and architecture are **not** dependencies.

## 2. Components

```text
Public API              DnstapClient · DnstapListener · DnstapOptions
Sending                 Bounded queue · Writer loop · Reconnect
Receiving               Accept loop · Handshake tasks · Reader loop · Message stream
Dnstap serialization    Public message model · Mapping · Google.Protobuf (internal)
Internal Frame Streams  Session · Negotiation · Frame codec · Control codec · FrameReader · FrameWriter
Diagnostics             Meter counters · ILogger
Transport               TCP · TLS · Unix domain socket · File/Stream
```

In v1, `DnstapClient` sends and dials and `DnstapListener` receives and listens. Reversed topology remains open.

## 3. Repository layout

```text
Dnstap.NET/
├── src/Dnstap/
│   ├── Dnstap.csproj
│   ├── DnstapClient.cs
│   ├── DnstapListener.cs
│   ├── DnstapOptions.cs
│   ├── Messages/DnstapMessage.cs
│   ├── Messages/DnstapSerializer.cs
│   ├── Protocol/FrameCodec.cs
│   ├── Protocol/ControlCodec.cs
│   ├── Protocol/FrameReader.cs
│   ├── Protocol/FrameWriter.cs
│   ├── Protocol/FrameSession.cs
│   ├── Schema/dnstap.proto
│   ├── Schema/Dnstap.g.cs
│   ├── Transport/TransportFactory.cs
│   ├── Diagnostics/DnstapMetrics.cs
│   └── Internal/WriterLoop.cs
├── tests/Dnstap.Tests/
├── tests/Dnstap.InteropTests/Fixtures/
└── examples/Dnstap.Demo/
```

All types under Protocol, Schema, Transport, Diagnostics and Internal remain internal unless a concrete requirement justifies exposure. The public `DnstapMessage` maps to generated protobuf types. A schema-mapping golden test must detect newly added schema fields not mapped into the public model.

## 4. Protocol invariants

| Invariant | Requirement |
|---|---|
| Startup | READY → ACCEPT → START for bidirectional sessions |
| Shutdown | STOP → FINISH for bidirectional sessions |
| File mode | START → DATA → STOP |
| Content type | Send `protobuf:dnstap.Dnstap`; byte-exact comparison; START may omit type; reject >1 START type |
| Framing | Network-byte-order unsigned 32-bit lengths |
| Reading | Partial reads and premature EOF handled explicitly |
| Allocation | Validate frame lengths before allocating |
| Limits | Control payload ≤512 bytes; CONTENT_TYPE field ≤256 bytes; default receive data ≤1 MiB and send data ≤96 KiB, independently configurable |
| Integers | Range-check uint32 before conversion or slicing |
| State | Reject data before START and invalid transitions; after START ignore unknown frame types, but reject unknown field types |
| Required fields | Check `IsInitialized()` for decoded protobuf messages; count and skip invalid payloads |
| Ownership | One session owns transport and receive state |
| Concurrency | With asynchronous delivery, only writer loop performs transport writes; callers enqueue |
| Timeouts | Handshake, each write and FINISH wait bounded |
| Accept isolation | Handshakes run per connection outside accept loop |
| Cleanup | Close transport on failed handshake or reader exit |
| Reconnect | New session; no protocol state carried from previous session |
| Trust | Invalid custom TLS trust anchor is fatal; custom trust store has no silent system-root fallback |
| Receiver limits | Cap active connections; a full receiver channel pauses reads and induces TCP backpressure |

Each applicable invariant must eventually have coverage. **Do not claim coverage until corresponding tests exist and have passed.**

## 5. Public API contract

| Method | Contract |
|---|---|
| `ConnectAsync` | Connect and finish handshake within timeout |
| `SendAsync` | Accept into delivery pipeline; nonblocking policy behaves like TrySend; blocking only when enabled |
| `TrySend` | Immediate queue acceptance; return false and count loss when full |
| `FlushAsync` | Wait until accepted messages at an explicit synchronization boundary are written/flushed, honoring cancellation |
| `DisposeAsync` | Stop acceptance, drain until deadline, count remainder as dropped, close |

The listener exposes `IAsyncEnumerable<DnstapMessage>` or equivalent, with remote endpoint and negotiated type metadata per message. Queue `BoundedChannelFullMode.Wait` plus `TryWrite` distinguishes rejection: the channel drop modes can return success for discarded entries. Enqueued is not equivalent to remotely delivered. Explicitly specify DNS transport (UDP/TCP/DoT/DoH/DoQ); do not infer it from the socket.

## 6. Implementation sequence

1. **Protocol:** Frame/control codecs and state, bounded reading, file mode and independent fixtures.
2. **Protobuf:** Vendor pinned dnstap.proto, generated C# code, public model/mapping, schema drift tests.
3. **Basic transport:** TCP/Unix sockets, genuine dnstap client/server demo, independent `fstrm_capture` and Go dnstap interoperability.
4. **Asynchronous delivery:** Bounded channel, drop accounting, opt-in blocking, cancellation, flushing and graceful drain.
5. **Reliability:** Reconnect, TLS, logging/metrics, DNS-server and collector interoperability.
6. **Performance:** Measure allocation, throughput, latency and memory, then optimize observed bottlenecks.

## 7. First milestone

First establish **file-mode** interoperability: read captures written by independent tools; write files decoded by `dnstap -r` and `fstrm_dump`. Then establish TCP sender → Frame Streams negotiation → protobuf dnstap payload → receiver stdout. Run the sender against `fstrm_capture`. The demo uses separate client/server tasks. Every `protobuf:dnstap.Dnstap` payload must be a serialized dnstap envelope; decoding failures are counted and skipped. Do not introduce queueing, TLS or reconnection into this milestone.

## 8. Validation evidence reported 2026-11-14

The following are the **project owner's prior reports**, not tests performed by this repository or this documentation commit.

| Claim | Reported finding | Method |
|---|---|---|
| Technitium DNS Server .NET 10 | `DnsServerCore.csproj` targets `net10.0` at `c11fec6` | Source inspection |
| Channel drop modes | DropNewest/DropOldest/DropWrite `TryWrite` returns true for drops; Wait returns false when full | .NET 10.0.115 test |
| Google.Protobuf unknown fields | Unknown field 99 preserved on reserialization | v36.2 test |
| Google.Protobuf unknown enum | `socket_protocol = 99`, presence flag retained, round-tripped | Same test |
| Proto2 required | Missing envelope `type` can be parsed; `IsInitialized()` false | Same test |
| NativeAOT | Native publish ran; IL3050 in `FeatureSetDescriptor` on unused schema path | NativeAOT publish |
| protobuf-net | Runtime model relies on dynamic IL; compile-time serializers experimental in 3.4.40 | Source/docs review |
| Control behavior | START may omit type; >1 rejected; unknown control field rejected; unknown frame type ignored after START by protocol | libfstrm 0.6.1 |
| Frame limits | Control 512; content type 256; default receiver 1 MiB; golang-dnstap receive threshold 96 KiB | C/Go source inspection |
| Reference defects | CoreDNS listener stall and race, mTLS trust-anchor error, Go reconnect/size and cleanup defects | Standalone repros reported |
| Existing .NET libraries | None located by shallow NuGet/GitHub search | Shallow search |

Earlier Google.Protobuf NativeAOT tests reportedly compiled its v36.2 source because NuGet was unreachable. Reconfirmation with a published NuGet package remains outstanding.

## 9. Open decisions

| Decision | Options | Tentative recommendation |
|---|---|---|
| Licence | AGPL-3.0-or-later or permissive | Permissive aids external integrations; the current repository's LICENSE is AGPL-3.0 |
| Reversed topology | Sender listens vs sender dials | Defer sender-listen and per-sink queues until later |
| Unidirectional sockets | Skip READY/ACCEPT | Decide from interoperability evidence |
| TLS counterparts | TCP/TLS outside Frame Streams | Add in reliability phase |
| Operational defaults | Queue capacity, timeout, drain | Determine experimentally in relevant phase |

This version is the governing baseline. Later revisions must explicitly distinguish changed decisions from evidence and open questions.
