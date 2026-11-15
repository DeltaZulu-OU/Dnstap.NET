# P2 writer performance baseline — corrected harness (15 November 2026)

## Source and method

The first benchmark at [run 43717103527](https://github.com/zbalkan/Dnstap.NET/actions/runs/43717103527) is **superseded**: its entry point silently ignored the workflow's CLI configuration, and its TCP receiver allocated a new DATA buffer for every record. It cannot be used as a controlled baseline.

The **corrected baseline** is [GitHub Actions run 43721174860](https://github.com/zbalkan/Dnstap.NET/actions/runs/43721174860), artifact `writer-benchmark-evidence`, on branch `perf/corrected-benchmark-baseline` based on production commit `6fdc41d7cf61321864f1a5dce79460e7b8c3633e`. The only code difference from this production commit was temporarily enabling the workflow on the benchmark branch; the writer and benchmark harness were unchanged.

BenchmarkDotNet 0.15.8 on Ubuntu 26.04.1, .NET 10.0.15, AMD EPYC 9V74 (2 physical / 4 logical cores). The benchmark log confirms `IterationCount=3`, `LaunchCount=1` and `WarmupCount=1` for all six cases. Short jobs on shared CI hardware are **smoke-performance measurements**, not stable production benchmarks or performance-regression gates.

| Raw DNS payload bytes | File mean | File standard deviation | File allocations | Loopback TCP mean | TCP standard deviation | TCP allocations |
|---:|---:|---:|---:|---:|---:|---:|
| 64 | 242.1 ns | 0.78 ns | 216 B/op | 1.823 μs | 0.0047 μs | 504 B/op |
| 1,024 | 322.6 ns | 5.84 ns | 1,176 B/op | 2.498 μs | 0.0360 μs | 1,464 B/op |
| 8,192 | 877.0 ns | 64.26 ns | 8,344 B/op | 6.208 μs | 0.4196 μs | 8,632 B/op |

The file-mode case uses `Stream.Null`: it estimates frame serialization, allocation, and writer overhead, **not physical disk throughput**. The TCP case uses an in-process loopback receiver. Its reusable DATA receive buffer eliminates one per-frame allocation, but BenchmarkDotNet's process-wide allocation measurement still includes background receiver and runtime allocations. TCP allocations are **not writer-only allocations**. The TCP mean includes transport scheduling and the receiver's work; it is not a network capacity measurement.

The earlier benchmark observations were collected on a different host and with CLI arguments ignored. Do not compute a speedup or slowdown across those measurements. Because each case has only three measured iterations, avoid treating small differences as significant.

## Optimization decision

**Keep the present single-owned-buffer, single-write, single-deadline implementation.** Payload-sized allocations are deliberate to keep data valid after timeouts on cancellation-ignoring streams. Do not introduce a buffer pool without an enforceable outstanding-I/O lifetime contract. The measurements by themselves do not justify batching, caching handshake control frames, or adding an uncontended lock fast path.

Before changing performance-sensitive production code, define a target workload (rate, payload distribution, slow-receiver backpressure, latency percentiles and memory limits), reproduce baseline and candidate on equivalent machines with identical benchmark arguments, and retain raw reports.

The BenchmarkDotNet project and its manually triggered workflow were retired after capturing this baseline. The measurements, limitations, and historical implementation remain documented here and recoverable from Git history.
