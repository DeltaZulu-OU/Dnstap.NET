# Dnstap.NET

Dnstap.NET is a planned managed .NET 10 dnstap library. Frame Streams protocol support is internal; it is **not** a separate Fstrm.NET package.

The repository currently contains the architecture, normative Frame Streams specification, and CI/test infrastructure. The protocol and protobuf implementation have not yet been merged.

- [Architecture Baseline v1.1](docs/ARCHITECTURE-v1.1.md)
- [Frame Streams protocol specification](docs/FRAMESTREAMS-PROTOCOL.md)
- [TDD, branching and CI policy](docs/TDD.md)

### Development

Keep `main` passing. Protocol test development proceeds in [`test/framestreams-protocol-specifications`](https://github.com/zbalkan/Dnstap.NET/tree/test/framestreams-protocol-specifications) through Red, Green and Refactor before integration. A Red branch may have failing tests by design; build and restore failures are not accepted.

`dotnet test tests/Dnstap.Tests/Dnstap.Tests.csproj --configuration Release`

Cross-language C/Go test fixtures and independent interoperability remain to be added. A green infrastructure check is **not** evidence of implemented protocol behavior.
