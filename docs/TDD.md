# TDD workflow

1. **Red:** On a feature branch, add a focused MSTest specification of observable behavior. Run CI and record the expected failure. Keep `main` green.
2. **Green:** Implement the smallest correct change. Do not weaken or skip assertions. Rerun all tests.
3. **Refactor:** Only after Green, remove complexity and duplication without changing behavior. Re-run the full suite and CI; correctness before clarity, clarity before performance.
4. **Interop:** Tapper must use the **public library API**. Compare output with independent dnstap/Frame Streams implementations. A library self-roundtrip is not external proof.
5. **Integrate:** Merge green work directly into `main` without a PR. Keep Red/Green commits on the feature branch, preserve them with a merge commit, and leave branch deletion to the owner.

Test valid and invalid proto2 records, deterministic packet/metadata round-trips, frame limits, fragmentation, file START/STOP, duplex READY/ACCEPT/START/STOP/FINISH, cancellation, failures and cleanup. A local stream write does not prove receipt. Use fixed known fixture bytes and exact event counts.

For each change, choose relevant happy-path, negative, null/empty, boundary, exception, state, idempotency, dependency-failure, resource, concurrency and regression scenarios. Keep unit tests independent of external services using controlled streams; reserve DNS-collector and Go processes for CI integration. Coverage is diagnostic, not proof of correctness.

Commit messages begin with a past-tense verb without prefixes; group unrelated edits separately. Builds/test failures are routine debugging. Use `git bisect start --first-parent` to avoid intentional Red commits when checking integrated history.
