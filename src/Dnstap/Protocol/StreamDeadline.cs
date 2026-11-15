namespace Dnstap.Protocol;

// Centralizes deadline cancellation while keeping request state out of closures.
internal static class StreamDeadline
{
    internal static Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, TimeSpan? timeout,
        CancellationToken cancellationToken, string description = "Frame Streams write",
        TimeProvider? timeProvider = null) =>
        ExecuteAsync((stream, data), static (state, token) => state.stream.WriteAsync(state.data, token),
            timeout, cancellationToken, description, timeProvider);

    internal static Task ReadExactlyAsync(Stream stream, Memory<byte> data, TimeSpan timeout,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null) =>
        ExecuteAsync((stream, data), static (state, token) => state.stream.ReadExactlyAsync(state.data, token),
            timeout, cancellationToken, "Frame Streams control read", timeProvider);

    internal static Task FlushAsync(Stream stream, TimeSpan? timeout, CancellationToken cancellationToken,
        TimeProvider? timeProvider = null) =>
        ExecuteAsync(stream, static (state, token) => new ValueTask(state.FlushAsync(token)),
            timeout, cancellationToken, "Frame Streams flush", timeProvider);

    private static async Task ExecuteAsync<TState>(TState state,
        Func<TState, CancellationToken, ValueTask> operation, TimeSpan? timeout,
        CancellationToken cancellationToken, string description, TimeProvider? timeProvider)
    {
        if (timeout is null)
        {
            await operation(state, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var deadline = new CancellationTokenSource(timeout.Value, timeProvider ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            // WaitAsync also bounds streams that disregard cancellation.
            await operation(state, linked.Token).AsTask()
                .WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{description} timed out.");
        }
    }
}
