# Typed dnstap record construction

## Decision

Dnstap.NET accepts native .NET metadata through `DnsLogEntry` and `DnstapRecordFactory.Create`. The consuming application is responsible for parsing text log fields; the library does not accept string-formatted addresses, ports, event names or transport names. This is an intentional breaking refinement of the alpha API introduced in `7557950`, and avoids parsing ambiguities such as abbreviated, hexadecimal or octal IPv4 literals.

The generated official dnstap protobuf model remains the authoritative schema. The factory is a narrow convenience API, not a DNS parser, policy engine or second protocol model.

## Input semantics

| Typed property | .NET type | Official protobuf field |
|---|---|---|
| `EventType` | `Message.Types.Type` (required) | `message.type` |
| `ClientAddress` | `IPAddress?` | `message.query_address` (initiator) |
| `ServerAddress` | `IPAddress?` | `message.response_address` (responder) |
| `ClientPort` | `ushort?` | `message.query_port` |
| `ServerPort` | `ushort?` | `message.response_port` |
| `Transport` | `SocketProtocol?` | `message.socket_protocol` |
| `Timestamp` | `DateTimeOffset?` | Event-direction `query_time_*` or `response_time_*` |
| `QueryPacket` | `ReadOnlyMemory<byte>` | `message.query_message` |
| `ResponsePacket` | `ReadOnlyMemory<byte>` | `message.response_message` |

The `ClientAddress` and `ServerAddress` names indicate message **initiator and responder**, not necessarily the process running the DNS server. `EventType` distinguishes query/response direction and observer perspective; it cannot be inferred solely from DNS packet flags. For consistency, both provided IP addresses must use the same address family. IPv4-mapped IPv6 addresses remain IPv6 unless the caller explicitly normalizes them; mixed mapped-IPv6/IPv4 values are rejected.

Scoped IPv6 addresses with nonzero `ScopeId` are rejected, because `GetAddressBytes()` cannot represent the interface scope in the official dnstap schema. The consumer must explicitly decide whether it is acceptable to discard a scope before constructing the entry. Optional null metadata remains absent in protobuf. Undefined event and transport enum values are rejected; numeric ports are range-limited by `ushort` at compile time. A nullable port set to zero is explicitly present, not omitted.

`Timestamp` is captured once, converted to Unix epoch seconds and 100-nanosecond-resolution fractional nanoseconds, and assigned to query or response time according to `EventType`. Pre-epoch timestamps are rejected. An observation with both query and response instants should set the official generated protobuf model fields directly.

Packet bytes are copied when supplied, not synthesized from QNAME/QTYPE/RCODE or other textual metadata. A metadata-only protobuf record is initialized and valid, but specific downstream consumers may need original packet bytes to decode DNS contents.

## Example

```csharp
using System.Net;
using Dnstap;

var record = DnstapRecordFactory.Create(new DnsLogEntry
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

No string-parsing overload, `TryCreate`, reflection-based mapper, synthetic DNS packet generator, application-specific `extra` schema, or server-specific adapter is provided. Consumers needing other official fields (policy, identity, version, HTTP protocol, query zone, separate query/response timestamps) may manipulate the generated protobuf record directly.

See `src/Dnstap/Schema/dnstap.proto` for authoritative field semantics.
