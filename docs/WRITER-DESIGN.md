# Writer API and I/O lifetime decisions

## Public API decisions (before first package release)

- Keep the namespace `Dnstap` and the generated protobuf message `global::Dnstap.Dnstap`. This follows the pinned schema's generated API; renaming would introduce a custom schema surface. Applications may use `using DnstapRecord = global::Dnstap.Dnstap;` for clarity. Revisit only in a separately justified breaking release.
- Keep `leaveOpen: true` for both constructors. The caller supplies and retains ownership of the stream. `leaveOpen: false` opts into disposing the stream when the writer is disposed. Never infer ownership from file or duplex mode.
- Keep `operationTimeout` as a per-operation bound in duplex mode. A DATA frame (header and encoded payload) is one write with one deadline. The gate wait has a separate deadline. Control header and control body reads still have separate deadlines.
- File mode has no implicit I/O or lock timeout, including during `DisposeAsync`. Callers that use file-like network or remote streams must plan for stalled operations.

## Cancellation and incomplete I/O

A duplex timeout bounds **the caller's wait**, not the lifetime of arbitrary custom `Stream` operations. A stream can disregard cancellation; the in-flight task can continue after `WriteAsync` returns a `TimeoutException`. In such cases the writer enters `Faulted`, never sends a further DATA frame, and must not be reused. Owned-frame data stays in a separate managed array, so late access does not reuse or overwrite a shared header. The stream owner remains responsible for handling any transport-level pending operations and choosing whether and when to close the transport. Disposing an owned stream can overlap pending noncooperative I/O.

If both the caller's cancellation and the timeout have fired, cancellation takes precedence. Any partial frame on a failed stream is terminal to that stream session; never attempt to retry the remainder through the same writer. Delivery guarantees, retry and reconnection remain out of scope.

## Performance decision

The previous implementation wrote a shared four-byte header followed by an individually allocated protobuf payload under separate deadlines. The current implementation serializes directly into one independently owned `length + payload` buffer using `IMessage.WriteTo(Span<byte>)`, then performs one stream write under one deadline. This saves a protobuf-array allocation, one stream operation and one timeout per DATA frame while preserving wire bytes. It intentionally changes the prior per-header/per-payload timeout to a per-frame timeout.

A linked CTS is created only for a cancelable caller token. Do not introduce pooled frame storage without an explicit guarantee that noncooperative asynchronous writes have ended before returning the buffer to the pool.

## Validation

Run the repository's MSTest specifications and Go/DNS-collector interoperability tests. Use `benchmarks/Dnstap.Benchmarks` with BenchmarkDotNet for allocations and throughput on the actual runtime and target machine. Compare file and loopback-duplex paths across payload sizes, and do not claim a performance improvement solely from allocation counts.
