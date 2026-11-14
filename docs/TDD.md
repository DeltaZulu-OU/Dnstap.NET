# Specification-first TDD workflow

The protocol specification is in [FRAMESTREAMS-PROTOCOL.md](FRAMESTREAMS-PROTOCOL.md). Follow **Red → Green → Refactor** in that order. The reference protocol, not an earlier Fstrm.NET class design, is the source of truth.

1. **Red:** Add a failing MSTest acceptance case grounded in a specific FS-xxx requirement. Compilation must succeed; runtime tests fail because the implementation is absent or wrong. Record the first failing test and preserve the expected wire bytes.
2. **Green:** Implement the smallest internal Dnstap.NET change that satisfies the selected test without weakening its assertion. Repeat until the selected slice passes.
3. **Refactor:** Simplify while maintaining all prior test results. Re-run the full test suite after refactoring.
4. **Interoperability:** Validate file and socket output against independent C/Go implementations. Self-roundtrip is insufficient as the only oracle.
5. **NativeAOT:** Once protobuf model is added, publish a smoke-test executable; don't claim AOT validation from unit tests alone.

The GitHub Actions workflow runs restore, Release build, and MSTest on every push, pull request, and manual dispatch. TRX results are uploaded on both success and failure. Initially, a red run is **expected** and is *not* to be hidden with \`continue-on-error\` or test skips. Do not enable branch protection requiring a green check until a genuine Green iteration. A red CI job is a normal engineering state, not a security incident.

Work only on feature branches. Do not merge a deliberately red branch. Commit messages use past-tense verbs without conventional-commit prefixes.

Current scope: protocol docs, internal compile-time contracts, executable red specifications, and CI. No runtime protocol implementation or public Dnstap API is being claimed.
