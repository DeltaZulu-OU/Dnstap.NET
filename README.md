# Dnstap.NET

Dnstap.NET is a managed .NET 10 dnstap library under development. Frame Streams is implemented **internally**, not as a separate Fstrm.NET project.

The first phase provides internal Frame Streams encoding, incremental decoding, and strict unidirectional file-mode reading/writing, with MSTest coverage. The public Dnstap API, Google.Protobuf schema/mapping, bidirectional TCP negotiation, and cross-language interoperability remain future milestones.

- [Architecture Baseline v1.1](docs/ARCHITECTURE-v1.1.md)
- [Internal Frame Streams protocol specification](docs/FRAMESTREAMS-PROTOCOL.md)
- [TDD and CI workflow](docs/TDD.md)
- [Future DNS-collector interoperability suite](docs/DNS-COLLECTOR-INTEROP.md)

## Development

The default branch must remain green. Develop experimental Red/Green/Refactor work on feature branches. After passing all applicable CI tests, merge validated changes directly into `main` without pull requests.

Run tests: `dotnet test tests/Dnstap.Tests/Dnstap.Tests.csproj --configuration Release`

Tests currently use deterministic internal literal frames, not independently captured C/Go fixtures. A passing suite does **not** yet establish protocol interoperability.
