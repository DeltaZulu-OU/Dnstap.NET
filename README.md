# Dnstap.NET

A planned fully managed .NET 10 dnstap library with internal Frame Streams protocol support.
The public API, protobuf serializer, network transports, and operational delivery loop are **not implemented yet**.

- [Frame Streams protocol specification](docs/FRAMESTREAMS-PROTOCOL.md)
- [TDD and CI workflow](docs/TDD.md)

The initial MSTest suite deliberately defines failing executable contracts. The **Red** CI state is
expected until internal framing and file-session implementations are added. Build or restore
errors are *not* an intended Red state and must be fixed.

Architecture authority: Dnstap.NET Architecture Baseline v1.1 (14 November 2026).

## Run specifications

`dotnet test tests/Dnstap.Tests/Dnstap.Tests.csproj -c Release`

The tests use independent literal frame bytes for wire-format assertions.
No independent C/Go interoperability fixtures are claimed yet.
