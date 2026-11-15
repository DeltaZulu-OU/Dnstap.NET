# P2 writer benchmark baseline (15 November 2026)

Source: GitHub Actions [run 43717103527](https://github.com/zbalkan/Dnstap.NET/actions/runs/43717103527), workflow artifact `writer-benchmark-evidence`. Commit: `7a15a273899cb5e3d9be50906d8f7c204bfcdbca`. BenchmarkDotNet 0.15.8, Ubuntu 26.04.1, .NET 10.0.15, Xeon 6975P (2 physical / 4 logical cores).

The original workflow **requested** `--job short --warmupCount 1 --iterationCount 3 --launchCount 1`, but the benchmark entry point did not forward its command-line arguments. These measurements therefore used BenchmarkDotNet defaults, not the documented short configuration. Moreover, TCP allocations include per-frame allocations made by the receiver. The figures below are retained only as **superseded historical observations**; do not use them as a valid controlled baseline:

| DNS payload bytes | File mean (ns) | File allocation (B/op) | Loopback TCP mean (ns) | TCP allocation (B/op) |
|---:|---:|---:|---:|---:|
| 64 | 199.7 | 216 | 893.1 | 600 |
| 1,024 | 309.8 | 1,176 | 1,414.9 | 2,528 |
| 8,192 | 876.7 | 8,344 | 3,860.8 | 16,851 |

## Interpretation and next decision

These are superseded single-run observations, not a before/after comparison, a workload capacity figure, or evidence of a regression. Re-run benchmarks after the command-line forwarding and receiver-buffer fixes, and replace this section with a new evidence-linked baseline. File mode writes to `Stream.Null`, so it measures serialization and writer overhead rather than disk throughput. The TCP case uses an in-process loopback peer and includes network/reader overhead and thread scheduling. Allocations scale with payload size because the writer owns a serialized frame buffer. The 8 KiB file-case standard deviation (59.25 ns) is non-negligible relative to its mean, so do not overinterpret small differences.

**Decision:** no further production optimization is warranted from these data alone. The single-buffer, single-deadline implementation remains. In particular, do not pool outgoing frames: a noncooperative asynchronous operation may retain the memory after timeout. To propose pooling or batching, first quantify a production target (messages/s, payload distribution, p95 latency, memory budget, behavior under slow receiver) and measure before/after with the same runner, pinned runtime and equivalent workload.

Run comparisons on dedicated, stable hardware when possible; shared GitHub runners are suitable for smoke measurements, not reliable performance gating. Preserve raw BenchmarkDotNet reports as evidence.
