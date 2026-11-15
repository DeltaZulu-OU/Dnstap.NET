namespace Dnstap.Protocol
{
    /// <summary>
    /// Centralizes deadline cancellation while keeping request state out of closures.
    /// </summary>
    internal static class StreamDeadline
    {
        internal static Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, TimeSpan? timeout,
            string description, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (timeout is null)
            {
                throw new ArgumentNullException(nameof(timeout));
            }

            ArgumentException.ThrowIfNullOrEmpty(description);
            ArgumentNullException.ThrowIfNull(timeProvider);
            return ExecuteAsync((stream, data), static (state, token) => state.stream.WriteAsync(state.data, token),
                timeout, description, timeProvider, cancellationToken);
        }

        internal static Task ReadExactlyAsync(Stream stream, Memory<byte> data, TimeSpan timeout,
            TimeProvider timeProvider, CancellationToken cancellationToken) =>
            ExecuteAsync((stream, data), static (state, token) => state.stream.ReadExactlyAsync(state.data, token),
                timeout, "Frame Streams control read", timeProvider, cancellationToken);

        internal static Task FlushAsync(Stream stream, TimeSpan? timeout, TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
            ExecuteAsync(stream, static (state, token) => new ValueTask(state.FlushAsync(token)),
                timeout, "Frame Streams flush", timeProvider, cancellationToken);

        private static async Task ExecuteAsync<TState>(TState state,
            Func<TState, CancellationToken, ValueTask> operation, TimeSpan? timeout,
            string description, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            if (timeout is null)
            {
                await operation(state, cancellationToken).ConfigureAwait(false);
                return;
            }

            using var deadline = new CancellationTokenSource(timeout.Value, timeProvider);
            using var linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token)
                : null;
            try
            {
                // WaitAsync also bounds streams that disregard cancellation.
                var effectiveToken = linked?.Token ?? deadline.Token;
                // Timeout bounds the wait, not completion of a noncooperative stream operation.
                await operation(state, effectiveToken).AsTask()
                    .WaitAsync(effectiveToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{description} timed out.");
            }
        }
    }
}
