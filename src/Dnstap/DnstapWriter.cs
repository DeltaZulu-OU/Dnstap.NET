using System.Buffers;
using System.Buffers.Binary;
using Dnstap.Protocol;
using Google.Protobuf;

namespace Dnstap;

public enum DnstapStreamMode { File, Duplex }

/// <summary>Writes dnstap Frame Streams records to a caller-provided stream.</summary>
public sealed class DnstapWriter : IAsyncDisposable
{
    private enum SessionState { Created, Started, Completed, Faulted }

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
    public DnstapWriter(Stream stream, bool leaveOpen = true,
        int maxDataLength = FrameCodec.DefaultMaxSendLength, TimeSpan? operationTimeout = null)
        : this(stream, DnstapStreamMode.File, leaveOpen, maxDataLength, operationTimeout) { }

    /// <summary>Creates a file or duplex writer. Duplex operations default to a 30-second timeout.</summary>
    public DnstapWriter(Stream stream, DnstapStreamMode mode, bool leaveOpen = true,
        int maxDataLength = FrameCodec.DefaultMaxSendLength, TimeSpan? operationTimeout = null)
        : this(stream, mode, leaveOpen, maxDataLength, operationTimeout, TimeProvider.System) { }

    internal DnstapWriter(Stream stream, DnstapStreamMode mode, bool leaveOpen,
        int maxDataLength, TimeSpan? operationTimeout, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!stream.CanWrite || (mode == DnstapStreamMode.Duplex && !stream.CanRead))
            throw new ArgumentException("The stream must be writable, and duplex streams must also be readable.", nameof(stream));
        if (maxDataLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDataLength));

        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
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
            throw new InvalidOperationException("Writer has already started.");

        try
        {
            await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // No bytes or protocol state were changed while waiting for the lock.
            Interlocked.Exchange(ref _startAttempted, 0);
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
                    ControlFrame accepted = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
                    if (accepted.Type != ControlTypes.Accept ||
                        (accepted.ContentTypes.Count != 0 &&
                         !accepted.ContentTypes.Any(type => type.Span.SequenceEqual(ControlTypes.DnstapContentType))))
                        throw new InvalidDataException("Collector did not accept the dnstap content type.");
                }

                await WriteControlAsync(ControlTypes.Start, cancellationToken, withContentType: true)
                    .ConfigureAwait(false);
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
            _gate.Release();
        }
    }

    /// <summary>Writes one initialized record. Concurrent writes are serialized; ordering is not guaranteed.</summary>
    public Task WriteAsync(Dnstap message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_state == SessionState.Faulted)
            throw new InvalidOperationException("Writer is faulted.");
        ArgumentNullException.ThrowIfNull(message);
        if (!message.IsInitialized())
            throw new ArgumentException("dnstap required fields are missing.", nameof(message));
        if (!Enum.IsDefined(message.Type) ||
            (message.Message is { } nested && !Enum.IsDefined(nested.Type)))
            throw new ArgumentException("dnstap required enum values are invalid.", nameof(message));
        if (message.Type == Dnstap.Types.Type.Message && message.Message is null)
            throw new ArgumentException("dnstap MESSAGE requires a nested message.", nameof(message));
        int size = message.CalculateSize();
        if (size < 1 || size > _maxDataLength)
            throw new ArgumentOutOfRangeException(nameof(message), "dnstap message exceeds the configured frame size.");
        // One owned frame buffer keeps header and payload stable even if a stream ignores cancellation.
        byte[] frame = new byte[checked(size + 4)];
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
                throw new InvalidOperationException("Writer is not started, is completed, or is faulted.");
            try
            {
                // A single deadline bounds the entire frame, not its header and payload separately.
                await StreamDeadline.WriteAsync(_stream, frame, WriteTimeout, cancellationToken, "Frame Streams data frame write", _timeProvider)
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
            _gate.Release();
        }
    }

    /// <summary>Sends STOP and, in duplex mode, waits for FINISH. An interrupted shutdown is terminal.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state == SessionState.Completed)
                return;
            if (_state != SessionState.Started)
                throw new InvalidOperationException("Writer must be started and healthy before completion.");
            await CompleteStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CompleteStartedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WriteControlAsync(ControlTypes.Stop, cancellationToken).ConfigureAwait(false);
            await StreamDeadline.FlushAsync(_stream, WriteTimeout, cancellationToken, _timeProvider).ConfigureAwait(false);
            if (_mode == DnstapStreamMode.Duplex)
            {
                ControlFrame finished = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
                if (finished.Type != ControlTypes.Finish)
                    throw new InvalidDataException("Expected FINISH after STOP.");
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
            cancellationToken, "Frame Streams control write", _timeProvider).ConfigureAwait(false);
    }

    private async Task<ControlFrame> ReadControlAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        try
        {
            await StreamDeadline.ReadExactlyAsync(_stream, header, _operationTimeout, cancellationToken, _timeProvider)
                .ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0)
                throw new InvalidDataException("Expected a Frame Streams control frame.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (length < 4 || length > FrameCodec.MaxControlLength)
                throw new InvalidDataException("Invalid Frame Streams control length.");
            byte[] payload = new byte[(int)length];
            await StreamDeadline.ReadExactlyAsync(_stream, payload, _operationTimeout, cancellationToken, _timeProvider)
                .ConfigureAwait(false);
            if (ControlCodec.TryDecode(new ReadOnlySequence<byte>(payload), out ControlFrame? control) != OperationStatus.Done)
                throw new InvalidDataException("Invalid Frame Streams control payload.");
            return control!;
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

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeRequested) != 0)
            throw new ObjectDisposedException(nameof(DnstapWriter));
    }

    /// <summary>
    /// Completes a healthy session, then releases an owned stream even after failure.
    /// A concurrent operation may still be unwinding when lock acquisition times out.
    /// Repeated concurrent disposal calls may return before the first finishes.
    /// File mode has no I/O or lock deadline; a stalled stream can prevent disposal from finishing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        bool acquired = false;
        try
        {
            await AcquireAsync(CancellationToken.None).ConfigureAwait(false);
            acquired = true;
            if (_state == SessionState.Started)
                await CompleteStartedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
            if (!_leaveOpen)
                await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
