# Dnstap.NET

A .NET 10 dnstap writer based on the official [dnstap protobuf schema](https://github.com/dnstap/dnstap.pb) and Frame Streams framing.

The package provides `DnstapWriter`, typed `DnsLogEntry`/`DnstapRecordFactory`, and generated protobuf record types. The caller provides its own writable stream or connected duplex stream; connection management, reconnection, DNS parsing and delivery policies are outside the package.

See [usage and lifecycle documentation](https://github.com/DeltaZulu-OU/Dnstap.NET#using-the-library) and the [protocol specification](https://github.com/DeltaZulu-OU/Dnstap.NET/blob/main/docs/FRAMESTREAMS-PROTOCOL.md).

Licensed under GPL-3.0-only. This is a prerelease API.
