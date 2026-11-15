# P3 evaluation — optional dnstap record construction

**Decision:** do not add a public factory or record wrapper at this stage. This is a writer-focused alpha and its existing API accepts the generated official `Dnstap` and `Message` messages. No independent need has been established for additional abstractions or lossy field defaults. A wrapper would introduce another public contract and risk failing to support the schema's multiple event perspectives, policy annotations and transport metadata.

## Schema constraints

The vendored official `src/Dnstap/Schema/dnstap.proto` is the authority. It distinguishes `Message.Type` values for queries and responses from authoritative, resolver, client, forwarder, stub, tool and update perspectives. `query_message` and `response_message` are the original binary DNS packets verbatim, **not** DNS names or UTF-8 text. Query and response timestamp fields consist of unsigned epoch **seconds** and a `fixed32` fractional **nanosecond** count. The schema recommends additional metadata per event kind without making every metadata field required by protobuf.

Using a single captured UTC `DateTimeOffset` avoids mismatched seconds/nanoseconds at a second boundary. Compute `seconds = captured.ToUnixTimeSeconds()`; reject pre-epoch times before converting to `ulong`. Compute `nanos = (captured.UtcTicks % TimeSpan.TicksPerSecond) * 100` for nonnegative epoch times. .NET `DateTimeOffset` only carries 100-nanosecond resolution, so do not imply a more precise timestamp than the input. Validate nanoseconds are in `[0,999999999]` if a convenience API is ever introduced. Use `ByteString.CopyFrom(rawDnsPacket)`, never `CopyFromUtf8` for DNS packets.

The generated protobuf model is intentionally not a DNS packet parser; preserve packet bytes and leave semantic DNS validation to the producing application. Do not infer query/response direction solely from whether a packet contains a response flag: the dnstap event type expresses an observation perspective, and an event may carry both query and response metadata.

## Future trigger

Consider an optional `DnstapRecordFactory` only after two real consumers demonstrate repeated error-prone record construction. Require a clear event-kind API (no single overloaded query/response shortcut that misrepresents schema perspectives), a timestamp type preserving actual precision, binary packet input, explicit socket metadata, policy handling, tests of every supported event family, and no modifications to the official protobuf model. Until then, direct generated-model construction is smaller and more faithful.

Evidence: the vendored dnstap schema and `tests/Dnstap.Tests/SchemaFidelityTests.cs`. This document is a scope decision, not a claimed protocol implementation gap.
