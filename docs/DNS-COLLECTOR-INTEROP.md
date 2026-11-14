# DNS-collector interoperability test plan

Status: planned acceptance suite, not yet implemented. Required once `DnstapListener` and protobuf decoding are available.

## Reference

- Upstream: https://github.com/dmachard/DNS-collector/releases/tag/v3.3.7
- Configuration specification: https://github.com/dmachard/DNS-collector/blob/v3.3.7/docs/loggers/logger_dnstap.md
- File input: https://github.com/dmachard/DNS-collector/blob/v3.3.7/docs/collectors/collector_fileingestor.md
- Target Linux x86-64 release archive: `DNS-collector_3.3.7_linux_amd64.tar.gz`, SHA-256 `7f16a995e3be6d7bb46974722a3f1336cc589fbfff2c2c3c9c57c0b4cbedcbbe` (SHA-256 published as the GitHub release asset digest; verified against the v3.3.7 release metadata on 2026-11-14). Version 3.3.7 replaces the earlier 3.3.1 pin; changing the upstream version or digest must trigger an explicit CI fixture review.

## Test topology

```text
Independent, pinned .fstrm capture (with known expected dnstap messages)
       |
       v
DNS-collector file-ingestor (watch-mode: dnstap)
       |
       v
DNS-collector dnstapclient logger (transport: tcp, compression: none)
       |
       v
Dnstap.NET listener bound to 127.0.0.1:ephemeral-port
       |
       v
Frame Streams READY / ACCEPT / START -> Google.Protobuf decode
       |
       v
MSTest assertions on received typed messages and connection lifecycle
```

DNS-collector's ordinary `dnstap` input is a **listener**, not a sender. Use `dnstapclient` as the sender. For deterministic inputs, use independently captured dnstap files from the fixture corpus; do not validate our reader by replaying files generated only by our own writer.

## Proposed CI harness

An integration test class/assembly fixture should start **one Dnstap.NET listener** on an ephemeral loopback port, then start one DNS-collector process for the test group using a generated YAML config with `file-ingestor` -> `dnstapclient`. Route each received message into a test-owned bounded channel. Only start the replay after the receiver is ready. Copy the fixture into the watched directory under a temporary extension and atomically rename it to `.fstrm` to trigger ingestion.

Download the pinned DNS-collector release in its **own integration-test job**, verify the archive SHA-256 **before extraction**, and fail on mismatch. Keep package-download failures separate from protocol test failures. Avoid unverified `latest` release URLs or dynamically inferred asset names.

The suite asserts exactly expected event counts, protobuf envelope validity, message type, identity/version if set, timestamps, addresses, DNS wire payload and byte-exact content-type negotiation. Use a bounded timeout, a deterministic end marker or known message count, and negative checks for additional messages. Capture DNS-collector stdout/stderr and Dnstap.NET diagnostics as CI artifacts.

Dispose the fixture in a `finally`/teardown path: terminate the collector process, close the listener, await all tasks, and delete temporary directories. Disable parallel execution for tests sharing the socket/process; independent tests should not share the same network reader. Ensure the test detects dropped or duplicated messages.

## Exit criteria and scope

This future suite validates **DNS-collector -> Dnstap.NET receiver** transport, handshake, and parsing. Independently run **Dnstap.NET sender -> DNS-collector listener** to validate the opposite direction. Neither direction should be conflated with generic malformed-frame unit tests. Stay on default uncompressed Frame Streams until the basic test passes; advanced compression is a separate compatibility feature.

Do not add this to the current file-mode Red/Green gate. Add it in the TCP/protobuf milestone, and require it to pass before claiming DNS-collector interoperability.
