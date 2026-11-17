# Dnstap.NET

A small .NET 10 library for writing [dnstap](https://dnstap.info/) DNS telemetry using the official protobuf schema and [Frame Streams](docs/FRAMESTREAMS-PROTOCOL.md). It is not a DNS server or a log collector.

- `src/Dnstap/`: reusable library, generated `dnstap.proto` types, and the public `DnstapWriter`.
- `src/Tapper/`: command-line test emitter that uses the library's public API.
- `tests/`: MSTest unit tests. CI also checks Tapper against independent Go dnstap tooling, DNS-collector, and libfstrm v0.6.1 receivers.

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

When a DNS server already exposes native .NET DNS metadata, use `new DnstapRecordFactory().Create(new DnsLogEntry { ... })`. The factory accepts typed IP addresses, numeric ports, official event/transport enums, and timestamps. The calling application is responsible for parsing any source strings; the factory never fabricates DNS packets:

```csharp
var factory = new DnstapRecordFactory("my-dns-server"u8.ToArray());
var record = factory.Create(new DnsLogEntry
{
    EventType = Message.Types.Type.ClientResponse,
    ClientAddress = System.Net.IPAddress.Parse("192.0.2.10"),
    ServerAddress = System.Net.IPAddress.Parse("192.0.2.53"),
    ClientPort = 53000,
    ServerPort = 53,
    Transport = SocketProtocol.Udp,
    Timestamp = DateTimeOffset.UtcNow
});
await writer.WriteAsync(record);
```

`ClientAddress` means DNS message initiator (`query_address`); `ServerAddress` means responder (`response_address`). This mapping must not be confused with the machine observing the event. The example's IP parsing happens outside the library. Packet fields remain unset unless original binary packet bytes are provided. `QueryZone` accepts an optional uncompressed, root-terminated DNS wire-format name (not a text domain name); the factory validates labels and total length, and rejects compression pointers. Set `QueryTimestamp` and `ResponseTimestamp` on an entry to preserve both event instants; legacy `Timestamp` still populates the event-direction field but conflicts with an explicit value for that same field. The factory copies optional producer `identity` and `version` bytes once and shares the immutable values across records; absent metadata is omitted. Construct one factory per producer and reuse it safely across concurrent events. See [record-construction rules and event-role mapping](docs/RECORD-CONSTRUCTION-EVALUATION.md#choosing-a-dnstap-event-type) for the authoritative, recursive-resolver, forwarder and DNS UPDATE observation perspectives, including PowerDNS Recursor specifics.

For a connected, readable/writable TCP stream, use `new DnstapWriter(stream, DnstapStreamMode.Duplex)`. The writer handles READY/ACCEPT/START and STOP/FINISH, but the caller owns the socket, TLS, reconnection and delivery policy. `CompleteAsync()` reports finalization failures; disposing a healthy duplex writer without explicit completion also performs a bounded shutdown.

Use `await writer.FlushAsync()` to flush a buffered underlying stream safely under the writer's serialization lock. It emits no Frame Streams frame, requires an active session, and faults the session if the underlying flush fails or is interrupted. Duplex flushes use the configured operation timeout; file mode has none.

`leaveOpen` defaults to `true`. The default maximum serialized DATA payload is **96 KiB (98,304 bytes)**, excluding the four-byte frame header; `maxDataLength` sets the per-record bound and oversized records are rejected before any DATA write.  duplex operations have a **30-second** per-operation timeout (configurable with `operationTimeout`). Concurrent writes are serialized without a guaranteed ordering. The official generated model provides protobuf serialization with `ToByteArray()` and parsing with `global::Dnstap.Dnstap.Parser.ParseFrom(bytes)`; these operate on protobuf messages, not complete Frame Streams files.

### Breaking change in 0.2.0

The static factory was replaced by a sealed instance with immutable producer metadata:

```csharp
// Before (0.1.0):
var record = DnstapRecordFactory.Create(entry);

// After (0.2.0):
var factory = new DnstapRecordFactory("server-a"u8.ToArray(), "1.2"u8.ToArray());
var record = factory.Create(entry);
```

The constructor accepts raw bytes (`ReadOnlyMemory<byte>`); it does not encode text or choose defaults. Empty values leave the corresponding protobuf fields absent. `DnsLogEntry` remains per-event input.

## Using Tapper

Tapper emits deterministic `example.com` DNS queries, with a distinct DNS transaction ID for each record.

- File: `dotnet run --project src/Tapper/Tapper.csproj -- file --output capture.fstrm --count 3`
- TCP collector: `dotnet run --project src/Tapper/Tapper.csproj -- tcp --host 127.0.0.1 --port 6000 --count 3`

Append `--no-complete` to exercise automatic graceful disposal instead of explicitly calling `CompleteAsync()`. A count of `0` emits an empty but complete Frame Streams session. Tapper exits nonzero on failure. For a self-contained dual-timestamp response fixture, append `--response-timestamps` in file mode; `--count 0 --response-timestamps` emits exactly one response. The CI checks both timestamps in the independent Go reader and the response timestamp in BIND `dnstap-read`. For a query-zone fixture, use file-mode `--count 1 --query-zone`; CI decodes the `query_zone` with Go and BIND.

## Writer lifetime and performance

The caller retains ownership of supplied streams by default (`leaveOpen: true`). Duplex DATA frames are written as a single length-prefixed buffer under one I/O deadline; file-mode streams have no implicit operation or disposal timeout. A duplex deadline bounds waiting, but cannot force a noncooperative underlying `Stream` operation to finish. After failure the writer becomes terminal; reconnection is the caller's responsibility. The generated protobuf type remains `global::Dnstap.Dnstap` (use an application-local alias for readability). See [writer design and API decisions](docs/WRITER-DESIGN.md).

## Build and verification

Run `dotnet test tests/Dnstap.Tests/Dnstap.Tests.csproj -c Release` for unit tests. GitHub Actions additionally validates Tapper file and TCP output against the pinned Go dnstap reader, **DNS-collector v3.3.7**, and **libfstrm v0.6.1**. The libfstrm TCP matrix exercises both explicit completion and disposal-driven completion; the pinned Go reader independently validates its finished captures. Receiver acceptance alone is not proof of strict protocol compliance.

Tapper also supports `unix --path /path/to/socket --count 3 [--no-complete]` for a Frame Streams duplex Unix-domain socket, exercised by FS-023 against libfstrm's Unix-socket capture receiver.

To reproduce FS-021 locally, first build Tapper in Release, then run `bash .github/interop/fstrm/run-target.sh tcp-explicit /absolute/path/to/fstrm_capture /absolute/path/to/dnstap /absolute/path/to/dotnet /absolute/evidence/directory`. Use `tcp-no-complete` to check disposal-driven completion. The capture tool must be built from libfstrm tag `v0.6.1` at commit `1d6e11d750dfa8d15419814dfe0aa86d80000741`; the Go dnstap reader is pinned to `ebb538e7d351a58861a8f348491828214a1d8db2`. The script validates receiver CLI flags, exact decoded records, distinct DNS IDs, and a negative control. No root privileges are required.

FS-022 additionally reads Tapper file captures with pinned libfstrm `fstrm_dump`, BIND `dnstap-read -y`, and Knot `kdig -G`, on Ubuntu 24.04. The file-reader matrix records their exact package versions and observed behavior on truncated files. It compares every field exposed by the reader, with the full Go record assertions still required for every target.

The vendored schema is pinned to [dnstap.pb commit 687347e](https://github.com/dnstap/dnstap.pb/commit/687347e16beebce5e075ff654871584597b2341c). [Frame Streams protocol and implementation rules](docs/FRAMESTREAMS-PROTOCOL.md).

FS-024 independently checks Tapper TCP and Unix duplex streams against Unbound 1.26.2 `unbound-dnstap-socket`. CI pins the source tarball SHA-256 and retains receiver output and decoded-record evidence for both completion modes. TLS interop is deferred; socket connectivity and stream lifecycle are owned by the caller.

## License

[GNU General Public License version 3 only (GPL-3.0-only)](LICENSE).
