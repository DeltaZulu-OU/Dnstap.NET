namespace Dnstap.Protocol;

// Centralizes cancellation and deadline behavior for both Frame Streams senders.
// The static delegates avoid per-write closure allocations.
internal static class StreamDeadline
{
    internal static Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, TimeSpan? timeout,
        CancellationToken cancellationToken, string description = "Frame Streams write") =>
        ExecuteAsync((stream, data), static (state, token) => state.stream.WriteAsync(state.data, token),
            timeout, cancellationToken, description);

    internal static Task ReadExactlyAsync(Stream stream, Memory<byte> data, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync((stream, data), static (state, token) => state.stream.ReadExactlyAsync(state.data, token),
            timeout, cancellationToken, "Frame Streams control read");

    internal static Task FlushAsync(Stream stream, TimeSpan? timeout, CancellationToken cancellationToken) =>
        ExecuteAsync(stream, static (state, token) => new ValueTask(state.FlushAsync(token)),
            timeout, cancellationToken, "Frame Streams flush");

    private static async Task ExecuteAsync<TState>(TState state,
        Func<TState, CancellationToken, ValueTask> operation, TimeSpan? timeout,
        CancellationToken cancellationToken, string description)
    {
        if (timeout is null)
        {
            await operation(state, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout.Value);
        try
        {
            // WaitAsync also bounds streams that disregard cancellation.
            await operation(state, deadline.Token).AsTask()
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{description} timed out.");
        }
    }
}
