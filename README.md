# Dnstap.NET

**Alpha.** A small .NET 10 library for writing [dnstap](https://dnstap.info/) DNS telemetry using the official protobuf schema and [Frame Streams](docs/FRAMESTREAMS-PROTOCOL.md). It is not a DNS server or a log collector.

- `src/Dnstap/`: reusable library, generated `dnstap.proto` types, and the public `DnstapWriter`.
- `Tapper/`: command-line test emitter that uses the library's public API.
- `tests/`: MSTest unit tests. CI also checks Tapper against independent Go dnstap tooling and DNS-collector.

## Using the library

Reference `src/Dnstap/Dnstap.csproj` from a .NET 10 application. Supply the original DNS packet bytes and event metadata; Dnstap.NET does **not** parse DNS packets or establish connections.

A minimal file example:

```csharp
using Dnstap;
using Google.Protobuf;

byte[] dnsQuery = Convert.FromHexString(
    "000101000001000000000000076578616D706C6503636F6D0000010001");

await using var file = File.Create("capture.fstrm");
await using var writer = new DnstapWriter(file);
await writer.StartAsync();
await writer.WriteAsync(new global::Dnstap.Dnstap
{
    Type = global::Dnstap.Dnstap.Types.Type.Message,
    Message = new Message
    {
        Type = Message.Types.Type.ClientQuery,
        QueryMessage = ByteString.CopyFrom(dnsQuery)
    }
});
await writer.CompleteAsync();
```

For a connected, readable/writable TCP stream, use `new DnstapWriter(stream, DnstapStreamMode.Duplex)`. The writer handles READY/ACCEPT/START and STOP/FINISH, but the caller owns the socket, TLS, reconnection and delivery policy. `CompleteAsync()` reports finalization failures; disposing a healthy duplex writer without explicit completion also performs a bounded shutdown.

`leaveOpen` defaults to `true`. The default maximum encoded record size is **96 KiB** (configurable with `maxDataLength`); duplex operations have a **30-second** per-operation timeout (configurable with `operationTimeout`). Concurrent writes are serialized without a guaranteed ordering. The official generated model provides protobuf serialization with `ToByteArray()` and parsing with `global::Dnstap.Dnstap.Parser.ParseFrom(bytes)`; these operate on protobuf messages, not complete Frame Streams files.

## Using Tapper

Tapper emits deterministic `example.com` DNS queries, with a distinct DNS transaction ID for each record.

- File: `dotnet run --project Tapper/Tapper.csproj -- file --output capture.fstrm --count 3`
- TCP collector: `dotnet run --project Tapper/Tapper.csproj -- tcp --host 127.0.0.1 --port 6000 --count 3`

Append `--no-complete` to exercise automatic graceful disposal instead of explicitly calling `CompleteAsync()`. A count of `0` emits an empty but complete Frame Streams session. Tapper exits nonzero on failure.

## Build and verification

Run `dotnet test tests/Dnstap.Tests/Dnstap.Tests.csproj -c Release` for unit tests. GitHub Actions additionally validates Tapper file and TCP output against the pinned Go dnstap reader and **DNS-collector v3.3.7**; collector acceptance alone is not proof of strict protocol compliance.

The vendored schema is pinned to [dnstap.pb commit 687347e](https://github.com/dnstap/dnstap.pb/commit/687347e16beebce5e075ff654871584597b2341c). [Frame Streams protocol and implementation rules](docs/FRAMESTREAMS-PROTOCOL.md).
