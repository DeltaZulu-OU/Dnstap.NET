# Dnstap writer benchmarks

Run in Release configuration on a stable host:

`dotnet run -c Release --project benchmarks/Dnstap.Benchmarks/Dnstap.Benchmarks.csproj`

The benchmarks measure the public `DnstapWriter.WriteAsync` API using either `Stream.Null` (file mode, writer/serialization overhead) or a genuine TCP loopback peer (duplex, including framing, cancellation setup and network transport). Inputs vary between 64, 1024 and 8192 bytes of DNS payload.

BenchmarkDotNet reports throughput/latency and managed allocations. Run on the same runtime, machine and configuration for before/after comparisons; record the checked-out commit. The benchmark suite intentionally is not part of the normal CI protocol run because timing data from shared CI runners is noisy. Loopback results include OS scheduling/network costs and cannot isolate timer overhead.

The previous two-write design is available at commit `79518826da09ec3cb98ac4431840779f60ace601` (it contains the separate `_dataHeader` write). Use the full commit SHA rather than the superseded `babec6b` reference. For a valid before/after comparison, copy this benchmark harness to the historical checkout, run identical command-line arguments, and preserve both reports.

The entry point forwards command-line options to `BenchmarkSwitcher`, including `--filter`, `--job`, and iteration settings. The loopback peer reuses one receive buffer sized for its configured message; process-wide allocation counters can still include background receive activity. Treat TCP allocations as **end-to-end process allocations**, not writer-only allocations.
