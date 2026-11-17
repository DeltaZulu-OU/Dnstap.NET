using System.Buffers;
using System.Buffers.Binary;
using Dnstap.Protocol;
using Google.Protobuf;

namespace Dnstap
{
    public enum DnstapStreamMode { File = 0, Duplex = 1 }

    /// <summary>Writes dnstap Frame Streams records to a caller-provided stream.</summary>
    public sealed class DnstapWriter : IAsyncDisposable
    {
        private enum SessionState { Created = 0, Started = 1, Completed = 2, Faulted = 3 }

        private readonly Stream _stream;
        private readonly DnstapStreamMode _mode;
        private readonly bool _leaveOpen;
        private readonly int _maxDataLength;
        private readonly TimeSpan _operationTimeout;
        private readonly TimeProvider _timeProvider;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ArrayBufferWriter<byte> _controlBuffer = new();
        private SessionState _state;
        private int _startAttempted;
        private int _disposeRequested;

        /// <summary>Creates a file-mode writer. A caller-provided stream remains open by default.</summary>
        /// <param name="stream"></param>
        /// <param name="leaveOpen"></param>
        /// <param name="maxDataLength"></param>
        /// <param name="operationTimeout"></param>
        public DnstapWriter(Stream stream, bool leaveOpen = true,
            int maxDataLength = FrameCodec.DefaultMaxSendLength, TimeSpan? operationTimeout = null)
            : this(stream, DnstapStreamMode.File, leaveOpen, maxDataLength, operationTimeout) { }

        /// <summary>Creates a file or duplex writer. Duplex operations default to a 30-second timeout.</summary>
        /// <param name="stream"></param>
        /// <param name="mode"></param>
        /// <param name="leaveOpen"></param>
        /// <param name="maxDataLength"></param>
        /// <param name="operationTimeout"></param>
        public DnstapWriter(Stream stream, DnstapStreamMode mode, bool leaveOpen = true,
            int maxDataLength = FrameCodec.DefaultMaxSendLength, TimeSpan? operationTimeout = null)
            : this(stream, mode, leaveOpen, maxDataLength, operationTimeout, TimeProvider.System) { }

        internal DnstapWriter(Stream stream, DnstapStreamMode mode, bool leaveOpen,
            int maxDataLength, TimeSpan? operationTimeout, TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(timeProvider);
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (!stream.CanWrite || (mode == DnstapStreamMode.Duplex && !stream.CanRead))
            {
                throw new ArgumentException("The stream must be writable, and duplex streams must also be readable.", nameof(stream));
            }

            if (maxDataLength <= 0 || maxDataLength > Array.MaxLength - sizeof(uint))
            {
                throw new ArgumentOutOfRangeException(nameof(maxDataLength),
                    "Maximum DATA payload must leave room for its four-byte frame header.");
            }

            _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
            if (_operationTimeout <= TimeSpan.Zero || _operationTimeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(operationTimeout));
            }

            _timeProvider = timeProvider;
            _stream = stream;
            _mode = mode;
            _leaveOpen = leaveOpen;
            _maxDataLength = maxDataLength;
        }

        private TimeSpan? WriteTimeout => _mode == DnstapStreamMode.Duplex ? _operationTimeout : null;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            // Claim before waiting: a competing start must fail immediately, not time out in negotiation.
            if (Interlocked.CompareExchange(ref _startAttempted, 1, 0) != 0)
            {
                throw new InvalidOperationException("Writer has already started.");
            }

            try
            {
                await AcquireAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // No bytes or protocol state were changed while waiting for the lock.
                _ = Interlocked.Exchange(ref _startAttempted, 0);
                throw;
            }

            try
            {
                ThrowIfDisposed();
                try
                {
                    if (_mode == DnstapStreamMode.Duplex)
                    {
                        await WriteControlAsync(ControlTypes.Ready, cancellationToken, withContentType: true)
                            .ConfigureAwait(false);
                        // A buffered duplex transport must expose READY before the peer can send ACCEPT.
                        await StreamDeadline.FlushAsync(_stream, WriteTimeout, _timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                        var accepted = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
                        if (accepted.Type != ControlTypes.Accept ||
                            (accepted.ContentTypes.Count != 0 &&
                             !accepted.ContentTypes.Any(type => type.Span.SequenceEqual(ControlTypes.DnstapContentType))))
                        {
                            throw new InvalidDataException("Collector did not accept the dnstap content type.");
                        }
                    }

                    await WriteControlAsync(ControlTypes.Start, cancellationToken, withContentType: true)
                        .ConfigureAwait(false);
                    if (_mode == DnstapStreamMode.Duplex)
                    {
                        // START must be available to receivers that buffer before reading DATA.
                        await StreamDeadline.FlushAsync(_stream, WriteTimeout, _timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    _state = SessionState.Started;
                }
                catch
                {
                    _state = SessionState.Faulted;
                    throw;
                }
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        /// <summary>Writes one initialized record. Concurrent writes are serialized; ordering is not guaranteed.</summary>
        /// <remarks>The caller must not mutate the protobuf record during validation and serialization.
        /// Its encoded frame is copied before asynchronous stream I/O begins.</remarks>
        /// <param name="message"></param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public Task WriteAsync(Dnstap message, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_state == SessionState.Faulted)
            {
                throw new InvalidOperationException("Writer is faulted.");
            }

            ArgumentNullException.ThrowIfNull(message);
            if (!message.IsInitialized())
            {
                throw new ArgumentException("dnstap required fields are missing.", nameof(message));
            }

            if (!Enum.IsDefined(message.Type) ||
                (message.Message is { } nested && !Enum.IsDefined(nested.Type)))
            {
                throw new ArgumentException("dnstap required enum values are invalid.", nameof(message));
            }

            if (message.Type == Dnstap.Types.Type.Message && message.Message is null)
            {
                throw new ArgumentException("dnstap MESSAGE requires a nested message.", nameof(message));
            }

            var size = message.CalculateSize();
            if (size < 1 || size > _maxDataLength)
            {
                throw new ArgumentOutOfRangeException(nameof(message), "dnstap message exceeds the configured frame size.");
            }
            // One owned frame buffer keeps header and payload stable even if a stream ignores cancellation.
            var frame = new byte[checked(size + 4)];
            BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)size);
            message.WriteTo(frame.AsSpan(4));
            return WriteRecordAsync(frame, cancellationToken);
        }

        private async Task WriteRecordAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        {
            await AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (_state != SessionState.Started)
                {
                    throw new InvalidOperationException("Writer is not started, is completed, or is faulted.");
                }

                try
                {
                    // A single deadline bounds the entire frame, not its header and payload separately.
                    await StreamDeadline.WriteAsync(_stream, frame, WriteTimeout, "Frame Streams data frame write", _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    _state = SessionState.Faulted;
                    throw;
                }
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        /// <summary>Flushes pending stream bytes under the writer lock without emitting a Frame Streams frame.</summary>
        /// <param name="cancellationToken"></param>
        /// <exception cref="InvalidOperationException">The session is not started or is no longer healthy.</exception>
        /// <exception cref="ObjectDisposedException">The writer is disposed.</exception>
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (_state != SessionState.Started)
                {
                    throw new InvalidOperationException("Writer must be started and healthy before flushing.");
                }

                try
                {
                    await StreamDeadline.FlushAsync(_stream, WriteTimeout, _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    _state = SessionState.Faulted;
                    throw;
                }
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        /// <summary>Sends STOP and, in duplex mode, waits for FINISH. An interrupted shutdown is terminal.</summary>
        /// <param name="cancellationToken"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (_state == SessionState.Completed)
                {
                    return;
                }

                if (_state != SessionState.Started)
                {
                    throw new InvalidOperationException("Writer must be started and healthy before completion.");
                }

                await CompleteStartedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = _gate.Release();
            }
        }

        private async Task CompleteStartedAsync(CancellationToken cancellationToken)
        {
            try
            {
                await WriteControlAsync(ControlTypes.Stop, cancellationToken).ConfigureAwait(false);
                await StreamDeadline.FlushAsync(_stream, WriteTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
                if (_mode == DnstapStreamMode.Duplex)
                {
                    var finished = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
                    if (finished.Type != ControlTypes.Finish)
                    {
                        throw new InvalidDataException("Expected FINISH after STOP.");
                    }
                }
                _state = SessionState.Completed;
            }
            catch
            {
                _state = SessionState.Faulted;
                throw;
            }
        }

        private async Task WriteControlAsync(uint type, CancellationToken cancellationToken, bool withContentType = false)
        {
            _controlBuffer.ResetWrittenCount();
            FrameCodec.WriteControl(_controlBuffer, withContentType
                ? new ControlFrame(type, new ReadOnlyMemory<byte>[] { ControlTypes.DnstapContentType.ToArray() })
                : new ControlFrame(type));
            await StreamDeadline.WriteAsync(_stream, _controlBuffer.WrittenMemory, WriteTimeout,
                "Frame Streams control write", _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ControlFrame> ReadControlAsync(CancellationToken cancellationToken)
        {
            var header = new byte[8];
            try
            {
                await StreamDeadline.ReadExactlyAsync(_stream, header, _operationTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0)
                {
                    throw new InvalidDataException("Expected a Frame Streams control frame.");
                }

                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                if (length is < 4 or > FrameCodec.MaxControlLength)
                {
                    throw new InvalidDataException("Invalid Frame Streams control length.");
                }

                var payload = new byte[(int)length];
                await StreamDeadline.ReadExactlyAsync(_stream, payload, _operationTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                return ControlCodec.TryDecode(new ReadOnlySequence<byte>(payload), out var control) != OperationStatus.Done
                    ? throw new InvalidDataException("Invalid Frame Streams control payload.")
                    : control!;
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("Unexpected EOF during Frame Streams control exchange.", ex);
            }
        }

        private async Task AcquireAsync(CancellationToken cancellationToken)
        {
            if (_mode != DnstapStreamMode.Duplex)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            using var timeout = new CancellationTokenSource(_operationTimeout, _timeProvider);
            using var linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token)
                : null;
            try
            {
                await _gate.WaitAsync(linked?.Token ?? timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for the writer lock.");
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, nameof(DnstapWriter));

        /// <summary>
        /// Completes a healthy session, then releases an owned stream even after failure.
        /// A concurrent operation may still be unwinding when lock acquisition times out.
        /// Repeated concurrent disposal calls may return before the first finishes.
        /// File mode has no I/O or lock deadline; a stalled stream can prevent disposal from finishing.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            {
                return;
            }

            var acquired = false;
            try
            {
                await AcquireAsync(CancellationToken.None).ConfigureAwait(false);
                acquired = true;
                if (_state == SessionState.Started)
                {
                    await CompleteStartedAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                if (acquired)
                {
                    _ = _gate.Release();
                }
                if (!_leaveOpen)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
