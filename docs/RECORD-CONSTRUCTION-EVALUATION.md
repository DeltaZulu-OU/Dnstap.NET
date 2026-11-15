# Dnstap record construction from string-valued DNS logs

## Decision

A consumer requirement has now emerged: a DNS server has already generated string-valued log metadata and needs an ergonomic conversion to the generated official protobuf representation. The public `DnsLogEntry` record and `DnstapRecordFactory.Create` support this case. They do **not** replace, regenerate or modify the pinned official dnstap protobuf model. More complex server-specific adapters remain outside this library.

## Input and field semantics

`EventType` is required and must name one of the 14 official dnstap `Message.Type` events (case insensitive, e.g. `CLIENT_QUERY`, `RESOLVER_RESPONSE`). It describes the *observer perspective*, which cannot be inferred from a DNS header.

| Log property | Official protobuf field | Notes |
|---|---|---|
| `ClientAddress` | `message.query_address` | DNS message **initiator**, not necessarily the local server |
| `ServerAddress` | `message.response_address` | DNS message **responder**, not necessarily the local server |
| `ClientPort` | `message.query_port` | Decimal string; 0–65535 |
| `ServerPort` | `message.response_port` | Decimal string; 0–65535 |
| `Transport` | `message.socket_protocol` | UDP, TCP, DOT, DOH, DOQ, DNSCryptUDP or DNSCryptTCP |
| `Timestamp` | `message.query_time_{sec,nsec}` or `response_time_{sec,nsec}` | Query events map to query time; response events to response time |
| `QueryPacket` | `message.query_message` | Original DNS packet bytes, copied |
| `ResponsePacket` | `message.response_message` | Original DNS packet bytes, copied |

The factory sets `socket_family` from supplied addresses and rejects mismatched IPv4/IPv6 families. Null optional strings mean absent values. Invalid non-null strings **fail** rather than silently disappearing. Unknown event or transport names fail rather than defaulting. No DNS wire packet is fabricated from QNAME, QTYPE, RCODE or other textual metadata.

A single `DateTimeOffset` value determines both epoch seconds and 100-nanosecond-granularity fractional nanoseconds. The factory rejects pre-1970 instants, and no precision greater than the input is implied. This convenience timestamp belongs to the observed event's direction; the caller can use the generated protobuf model to supply separate query and response timestamps where needed.

## Example

```csharp
using Dnstap;

var record = DnstapRecordFactory.Create(new DnsLogEntry
{
    EventType = "CLIENT_RESPONSE",
    ClientAddress = "192.0.2.10",
    ServerAddress = "192.0.2.53",
    ClientPort = "53000",
    ServerPort = "53",
    Transport = "UDP",
    Timestamp = DateTimeOffset.UtcNow
});

await writer.WriteAsync(record);
```

This metadata-only observation is a valid initialized protobuf message, but the DNS message fields remain absent. Downstream readers may expect packet bytes for richer decoding; compatibility should be assessed for each target collector.

## Scope restrictions

There is intentionally no `TryCreate`, reflection-based field mapping, synthetic DNS packet encoder, server-specific adapter, or free-form `extra` payload schema. These would need independent requirements and tests. Direct use of generated protobuf messages remains available for policy metadata, HTTP protocol, `query_zone`, `extra`, identity/version, independent timestamps and other schema fields not represented by this narrow convenience input.

See `src/Dnstap/Schema/dnstap.proto` for the authoritative meaning of each field. All 14 event types and seven transport values are covered by conversion tests.
