# Dnstap.NET

**A small .NET 10 library for writing dnstap-format DNS logs from a .NET DNS server or proxy.** No DNS processing, log forwarding pipeline, or receiving service.

The host supplies the original DNS wire packets and metadata. Dnstap.NET will encode them using the pinned dnstap protobuf schema and emit them as Frame Streams to a caller-provided file or duplex stream. The only intended public operations are writing and completing output; generated protobuf schema types are the input model. The host owns connections, retry behavior, buffering and any drop policy.

**Current status:** Internal file-mode Frame Streams encoding/decoding and lifecycle tests are implemented and pass CI. Protobuf integration and the minimal production writer API are **not implemented yet**. The internal reader exists for validation; a public dnstap listener is not planned.

- [Current architecture and strict scope (v1.2)](docs/ARCHITECTURE-v1.2.md)
- [Normative Frame Streams protocol details](docs/FRAMESTREAMS-PROTOCOL.md)
- [Red–Green–Refactor and CI](docs/TDD.md)
- [Planned independent output interoperability tests](docs/DNS-COLLECTOR-INTEROP.md)
- [Earlier, superseded architecture (v1.1)](docs/ARCHITECTURE-v1.1.md)

Run the existing tests: `dotnet test tests/Dnstap.Tests/Dnstap.Tests.csproj --configuration Release`.

Passing internal frame tests is **not** evidence that external collectors can yet consume protobuf dnstap records. No pull requests are required for this repository; experimental branches may be merged directly once CI passes. The repository owner handles branch deletion.
