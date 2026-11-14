# Dnstap writer interoperability

Tapper is the sender. It generates three deterministic DNS queries through the public Dnstap.NET API.

- **Strict Go check:** CI runs the pinned golang-dnstap reader for file and duplex output. It verifies framing and decoded event content.
- **DNS-collector v3.3.7 check:** A separate CI job runs its dnstap TCP listener and file-ingestor, confirms three distinct DNS IDs in each JSON output, and checks collector logs for errors or dropped batches. The file is renamed from `.tmp` to `.fstrm` after Tapper completes.
- **Limits:** DNS-collector is tolerant of some malformed input. Passing it confirms collector compatibility, not strict protocol compliance. The internal codec specifications and independent Go job remain required.

The collector is pinned to release [v3.3.7](https://github.com/dmachard/DNS-collector/releases/tag/v3.3.7), archive `DNS-collector_3.3.7_linux_amd64.tar.gz`, SHA-256 `7f16a995e3be6d7bb46974722a3f1336cc589fbfff2c2c3c9c57c0b4cbedcbbe`.

Do not implement a .NET receiver, collector lifecycle, queues, or retries in the production library.
