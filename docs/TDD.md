# Specification-first TDD workflow

Authoritative sources: [Architecture Baseline v1.1](ARCHITECTURE-v1.1.md), [Frame Streams protocol specification](FRAMESTREAMS-PROTOCOL.md).

## Branch policy

**Keep `main` green.** Work on feature branches. Commit deliberately failing specifications to the feature branch only and run CI there. Never merge or fast-forward a Red branch into `main`. The earlier red tests accidentally committed to `main` are removed by a corrective forward commit; their history remains available. No force-push or history rewrite is necessary.

## Red → Green → Refactor

1. **Red**: add a focused behavioral MSTest case for a particular FS-xxx requirement on the feature branch. A meaningful Red test distinguishes an *incorrect result* from an unimplemented API. Prefer `OperationStatus.Done/NeedMoreData/InvalidData` for incremental parsing; include valid examples and exact limits so a decoder that rejects everything cannot pass. Ensure builds succeed in CI; tests fail for the intended behavior, not `NotImplementedException`.
2. **Green**: implement the minimum production behavior to make the case pass, without weakening or skipping assertions. Add positive and negative boundary checks before claiming a limit is verified.
3. **Refactor**: simplify implementation and internal API, then rerun the entire suite. Tests should specify wire bytes and external results rather than incidental internal buffer shapes or record equality.
4. **Interop gate**: validate against C/Go input/output; a self-roundtrip is never sufficient evidence.
5. **AOT gate**: once protobuf is introduced, publish/run a native executable under CI and verify warnings.

The workflow restores, builds and runs MSTest in Release on every branch push and on manual dispatch. Pull requests are not used for this repository. TRX artifacts upload even on failure. Repeated pushes to the same branch can cancel superseded runs using a branch-scoped concurrency key. Red is an expected state only on the feature branch: do not use `continue-on-error`, disable tests, or claim main is healthy while failing.

## Test design requirements

- Test both **acceptance and rejection** for every bounded length: 4/512-byte controls; 0/256-byte content types; 1 and exactly 1 MiB receive data; 1 and exactly 96 KiB send data. Use length-only prefixes for *early rejection* tests and full payloads for acceptance tests.
- Distinguish `NeedMoreData` from `InvalidData`; only the consuming session declares premature EOF malformed. Parsing from segmented `ReadOnlySequence<byte>` cannot require a whole input array.
- Exercise illegal file sequences (empty, STOP before START, READY/ACCEPT/FINISH, repeated START/STOP, trailing data), unknown future controls, NUL-containing content types, and cancellation.
- Verify writer disposal sends STOP exactly once, even under repeated calls; verify ownership and cleanup using an instrumented stream.
- Use `Assert.ThrowsExactly` and `Assert.ThrowsExactlyAsync` when exception assertions are appropriate. Do not write generic wrappers that hide unexpected exceptions.
- Favor behavior-focused tests over APIs fixed too early. Use internal `IBufferWriter<byte>` for output; do not mandate one allocated `byte[]` per frame.
- Tests that require an implemented protocol should only appear on the feature branch during Red. The default branch contains only passing tests and implementations; protocol work is integrated only once the corresponding feature branch is green.

Validated experimental work is integrated into `main` via a no-fast-forward merge commit without a pull request, retaining the individual implementation and test commits. The SDK and Actions dependencies are pinned. Restore/build warnings are errors across production and tests. Failed restore/build steps must be fixed rather than counted as successful Red results.

Commit messages start with a past-tense verb and no prefix. Keep independent changes in separate commits.
