# Dnstap.NET

A small .NET 10 library for writing **dnstap** DNS logs with Protocol Buffers and Frame Streams. It is **not** a DNS server, listener, queue, or log forwarder.

- `src/Dnstap`: pinned official `dnstap.proto`, generated C# protobuf model, and a writer for files or caller-provided duplex streams.
- `Tapper/`: standalone CLI that emits deterministic dnstap events through the **public library API**.
- `tests/`: MSTest and independent Go interoperability checks.

The DNS host supplies DNS packet bytes, event metadata, and the destination stream. The library serializes the protobuf message and handles Frame Streams; the host owns connections, retries, TLS, and delivery policy. Generated C# types expose standard Protobuf serialize/parse; there is no second message model.

`dotnet test tests/Dnstap.Tests/Dnstap.Tests.csproj -c Release`

`dotnet run --project Tapper -- file --output test.fstrm --count 3`

`dotnet run --project Tapper -- tcp --host 127.0.0.1 --port 6000 --count 3`

Use `--no-complete` to exercise graceful disposal without calling `CompleteAsync` explicitly.

CI verifies Tapper output with the pinned Go dnstap reader in both modes. The upstream schema is pinned to [dnstap.pb commit 687347e](https://github.com/dnstap/dnstap.pb/commit/687347e16beebce5e075ff654871584597b2341c).

[Scope](docs/ARCHITECTURE-v1.2.md) · [Protocol](docs/FRAMESTREAMS-PROTOCOL.md) · [TDD](docs/TDD.md) · [Interop](docs/DNS-COLLECTOR-INTEROP.md)

Keep `main` green. No PRs; branch deletion is handled by the repository owner.
