# Dnstap writer benchmarks

Run in Release configuration on a stable host:

`dotnet run -c Release --project benchmarks/Dnstap.Benchmarks/Dnstap.Benchmarks.csproj`

The benchmarks measure the public `DnstapWriter.WriteAsync` API using either `Stream.Null` (file mode, writer/serialization overhead) or a genuine TCP loopback peer (duplex, including framing, cancellation setup and network transport). Inputs vary between 64, 1024 and 8192 bytes of DNS payload.

BenchmarkDotNet reports throughput/latency and managed allocations. Run on the same runtime, machine and configuration for before/after comparisons; record the checked-out commit. The benchmark suite intentionally is not part of the normal CI protocol run because timing data from shared CI runners is noisy. Loopback results include OS scheduling/network costs and cannot isolate timer overhead.

The previous two-write design is retained in Git history at `babec6b`. Run this benchmark project against that commit only after copying the benchmark project to that checkout; the public API remains compatible.
