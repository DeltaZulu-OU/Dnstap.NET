# Writer interoperability checks

**Goal:** Verify that Tapper exercises Dnstap.NET and independent implementations consume its output. No .NET listener or forwarding pipeline. **CI currently verifies both modes against pinned golang-dnstap; DNS-collector remains a separate optional cross-implementation check.**

- **File:** `Tapper file --output test.fstrm --count 3` → independent Go/libfstrm decoder. Check content type, frame sequence, exact event count, packet bytes and metadata.
- **Socket:** Start a pinned DNS-collector **dnstap input listener** on loopback; `Tapper tcp --host 127.0.0.1 --port 6000 --count 3` → verify READY/ACCEPT/START/DATA/STOP/FINISH and decoded events. Tapper opens the socket; the library only operates on the connected stream.
- **Negative:** Invalid protobuf, frame oversize, incompatible ACCEPT, truncation/cancellation and write failure must fail clearly without claiming delivery.
- **CI:** Run Tapper as an independent subprocess; check its exit code and the **collector's** decoded output. Pin binaries, time out startup/shutdown, clean up and attach artifacts on failure. Do not claim remote receipt from successful local writes.

Reference collector: [DNS-collector v3.3.7](https://github.com/dmachard/DNS-collector/releases/tag/v3.3.7), [dnstap input configuration](https://github.com/dmachard/DNS-collector/blob/v3.3.7/docs/collectors/collector_dnstap.md). Linux amd64 archive `DNS-collector_3.3.7_linux_amd64.tar.gz`, SHA-256 `7f16a995e3be6d7bb46974722a3f1336cc589fbfff2c2c3c9c57c0b4cbedcbbe`.
