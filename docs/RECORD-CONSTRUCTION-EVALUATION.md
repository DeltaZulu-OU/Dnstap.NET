# Typed dnstap record construction

## Decision

Dnstap.NET accepts native .NET metadata through `DnsLogEntry` and `DnstapRecordFactory.Create` (0.1.0 static API, replaced in 0.2.0). The consuming application is responsible for parsing text log fields; the library does not accept string-formatted addresses, ports, event names or transport names. This is an intentional breaking refinement of the alpha API introduced in `7557950`, and avoids parsing ambiguities such as abbreviated, hexadecimal or octal IPv4 literals.

The generated official dnstap protobuf model remains the authoritative schema. The factory is a narrow convenience API, not a DNS parser, policy engine or second protocol model.

## Stable producer identity and version

`DnstapRecordFactory` is now a sealed immutable instance. The constructor accepts optional `ReadOnlyMemory<byte>` identity and version bytes, copies each nonempty value into an immutable `ByteString` once, and reuses those objects for each record. Empty values are omitted, with no inferred hostname or version. The factory can be reused concurrently because it retains no mutable per-event state. `DnsLogEntry` remains strictly per-event metadata and has no producer identity/version fields.

**Breaking API migration:**

```csharp
// Before (0.1.0)
var oldRecord = DnstapRecordFactory.Create(entry);

// After (0.2.0)
var factory = new DnstapRecordFactory("server-a"u8.ToArray(), "1.2"u8.ToArray());
var record = factory.Create(entry);
```

## Input semantics

| Typed property | .NET type | Official protobuf field |
|---|---|---|
| `EventType` | `Message.Types.Type` (required) | `message.type` |
| `ClientAddress` | `IPAddress?` | `message.query_address` (initiator) |
| `ServerAddress` | `IPAddress?` | `message.response_address` (responder) |
| `ClientPort` | `ushort?` | `message.query_port` |
| `ServerPort` | `ushort?` | `message.response_port` |
| `Transport` | `SocketProtocol?` | `message.socket_protocol` |
| `QueryTimestamp` | `DateTimeOffset?` | `message.query_time_*` |
| `ResponseTimestamp` | `DateTimeOffset?` | `message.response_time_*` |
| `Timestamp` | `DateTimeOffset?` | Event-direction `query_time_*` or `response_time_*` |
| `QueryPacket` | `ReadOnlyMemory<byte>` | `message.query_message` |
| `QueryZone` | `ReadOnlyMemory<byte>` | `message.query_zone` (uncompressed DNS wire-format name) |
| `ResponsePacket` | `ReadOnlyMemory<byte>` | `message.response_message` |

The `ClientAddress` and `ServerAddress` names indicate message **initiator and responder**, not necessarily the process running the DNS server. `EventType` distinguishes query/response direction and observer perspective; it cannot be inferred solely from DNS packet flags. For consistency, both provided IP addresses must use the same address family. IPv4-mapped IPv6 addresses remain IPv6 unless the caller explicitly normalizes them; mixed mapped-IPv6/IPv4 values are rejected.

Scoped IPv6 addresses with nonzero `ScopeId` are rejected, because `GetAddressBytes()` cannot represent the interface scope in the official dnstap schema. The consumer must explicitly decide whether it is acceptable to discard a scope before constructing the entry. Optional null metadata remains absent in protobuf. Undefined event and transport enum values are rejected; numeric ports are range-limited by `ushort` at compile time. A nullable port set to zero is explicitly present, not omitted.

`QueryTimestamp` and `ResponseTimestamp` independently map to the corresponding official protobuf fields at 100-nanosecond precision. The legacy `Timestamp` remains a shortcut for the event-direction field (query for query types, response for response types). Supplying `Timestamp` together with its matching explicit property is ambiguous and throws `ArgumentException`; it may coexist with the opposite-direction explicit timestamp. Pre-epoch timestamps are rejected in every property. The factory does not require the response instant to follow the query instant. Go's dnstap reader displays both values on a response record; BIND `dnstap-read -y` displays only the response-side timestamp and packet for `CLIENT_RESPONSE`, so BIND cannot independently verify the query timestamp in that output.

`QueryZone` accepts a complete uncompressed DNS wire-format name, including its terminating zero-length root label. Individual labels are at most 63 octets, and the complete name at most 255 octets. Compression pointers and reserved label encodings are rejected, as are missing roots and trailing bytes. The factory never parses or encodes textual domain names. An empty value omits the field; a single zero byte represents the DNS root. Nonempty bytes are copied.

Packet bytes are copied when supplied, not synthesized from QNAME/QTYPE/RCODE or other textual metadata. A metadata-only protobuf record is initialized and valid, but specific downstream consumers may need original packet bytes to decode DNS contents.

## Example

```csharp
using System.Net;
using Dnstap;

var factory = new DnstapRecordFactory("server-a"u8.ToArray());
var record = factory.Create(new DnsLogEntry
{
    EventType = Message.Types.Type.ClientResponse,
    ClientAddress = IPAddress.Parse("192.0.2.10"),
    ServerAddress = IPAddress.Parse("192.0.2.53"),
    ClientPort = 53000,
    ServerPort = 53,
    Transport = SocketProtocol.Udp,
    Timestamp = DateTimeOffset.UtcNow
});
await writer.WriteAsync(record);
```

`IPAddress.Parse` is used here only to construct sample input. Actual DNS-server consumers should supply their existing `IPAddress` instances; parsing and input normalization are outside Dnstap.NET.

## Deliberately excluded

No string-parsing overload, `TryCreate`, reflection-based mapper, synthetic DNS packet generator, application-specific `extra` schema, or server-specific adapter is provided. Consumers needing other official fields (policy, HTTP protocol, query zone) may manipulate the generated protobuf record directly.

See `src/Dnstap/Schema/dnstap.proto` for authoritative field semantics.
