# Dnstap.NET

A small .NET 10 library for writing and decoding **dnstap** messages with Protocol Buffers and Frame Streams. It is for use by .NET DNS servers and proxies; it is **not** a DNS server, listener service, or log forwarder.

- `src/Dnstap`: official pinned `dnstap.proto`, generated C# protobuf model, and file/duplex-stream writer.
- `Tapper`: syslog-`logger`-style CLI, at the repository root, to emit deterministic dnstap events.
- `tests`: unit and independent C/Go/collector interoperability tests.

The DNS host supplies packet bytes, event metadata, and the destination stream. Dnstap.NET validates/serializes the protobuf message and handles Frame Streams; the host handles connections, queues, reconnects, and delivery policy. Generated protobuf classes already support serialize/parse; no duplicate public model is planned.

**Current status:** Internal Frame Streams file framing is implemented. Protobuf, public writer, duplex sender and Tapper are next.

[Scope and steps](docs/ARCHITECTURE-v1.2.md) · [Frame Streams rules](docs/FRAMESTREAMS-PROTOCOL.md) · [TDD](docs/TDD.md) · [Interop](docs/DNS-COLLECTOR-INTEROP.md)

Run tests: `dotnet test tests/Dnstap.Tests/Dnstap.Tests.csproj -c Release`. CI must be green on `main`. No PRs; the repository owner deletes merged branches.
