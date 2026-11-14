# Dnstap writer interoperability test plan

Status: planned; not yet running. Governing scope: [Architecture Baseline v1.2](ARCHITECTURE-v1.2.md).

## Goal

Prove that **a .NET DNS server or proxy can write genuine dnstap logs** and that independently developed tools can decode them. Dnstap.NET is the **sender** in every required socket test. There is no plan to add a public .NET listener.

## Pinned independent implementations

- DNS-collector release: https://github.com/dmachard/DNS-collector/releases/tag/v3.3.7
- DNS-collector's [dnstap input collector](https://github.com/dmachard/DNS-collector/blob/v3.3.7/docs/collectors/collector_dnstap.md) listens on TCP or Unix sockets and can be configured without TLS for the test.
- DNS-collector's [file ingestor](https://github.com/dmachard/DNS-collector/blob/v3.3.7/docs/collectors/collector_fileingestor.md) can consume saved dnstap captures.
- Linux x86-64 release archive: `DNS-collector_3.3.7_linux_amd64.tar.gz`. SHA-256: `7f16a995e3be6d7bb46974722a3f1336cc589fbfff2c2c3c9c57c0b4cbedcbbe`. Pin the exact release and validate its checksum before extraction.
- Also use pinned `golang-dnstap`/`libfstrm` tools as independent file-mode decoding oracles where practicable.

## File-mode test

```text
Known DNS query/response wire bytes + schema-defined metadata
                |
                v
  Dnstap.NET protobuf serializer + file-mode Frame Streams writer
                |
                v
  Independent tool reading .fstrm, checking decoded dnstap fields
```

Assert exact message type, DNS wire packets, timestamps, addresses, ports, socket protocol and optional identity, where set. Check correctly serialized `dnstap.Dnstap` (not UTF-8 pseudo-logs), START/DATA/STOP, and clean file termination. Test malformed proto2 required fields and oversized payloads without output corruption.

## Duplex-stream test

```text
DNS-collector dnstap input listener (TCP loopback, compression: none)
              ^
              | ACCEPT, FINISH / READY, START, DATA, STOP
              |
.NET test host's connected NetworkStream
              ^
              |
        Dnstap.NET sender
```

Start one pinned DNS-collector process per test fixture, configure its **dnstap input collector**, and expose the listener on loopback. The .NET test host creates the socket/stream; Dnstap.NET performs only the Frame Streams sender handshake and writes protobuf messages. Use a bounded readiness deadline and run the sender after the collector is listening.

Verify that the independently implemented collector decodes the expected messages, including a known event count with no duplicates. Record collector stdout/stderr and output artifacts on failure; ensure teardown kills the process and frees the port. The fixture may reuse one connection across sequential tests only if events can be correlated and concurrent tests are disabled. Require a separate negative test for an incompatible content-type response.

CI runs a dedicated integration job with the pinned binary and SHA-256 verification. Download failures are not protocol failures. Avoid floating releases, external test services or assumptions of successful transmission based on local stream writes alone.

## Exit criteria

File-mode output must be readable by an independent implementation. Duplex output must negotiate and deliver correct protobuf records to DNS-collector (or an equivalent independent collector), and shutdown must follow STOP → FINISH. Never claim external interoperability from an internal writer/reader round-trip.

**Out of scope:** DNS-collector acting as the sender to a .NET listener, receiving/forwarding pipelines, collector process management in production, TLS implementation inside Dnstap.NET, logging queues, reconnect and metrics.
