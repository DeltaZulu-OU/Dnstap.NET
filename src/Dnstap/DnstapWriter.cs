using System.Buffers;
using System.Buffers.Binary;
using Dnstap.Protocol;
using Google.Protobuf;

namespace Dnstap;

public enum DnstapStreamMode { File, Duplex }

/// <summary>Emits dnstap records over Frame Streams. Callers own the transport unless leaveOpen is false.</summary>
public sealed class DnstapWriter : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly FrameSequenceWriter _frames;
    private readonly DnstapStreamMode _mode;
    private readonly int _maxDataLength;
    private readonly TimeSpan _operationTimeout;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private int _startAttempted;
    private int _disposed;
    private bool _started;
    private bool _faulted;
    private bool _finished;

    /// <summary>Creates a file-mode writer. The stream must be writable.</summary>
    public DnstapWriter(Stream stream, bool leaveOpen = true,
        int maxDataLength = FrameCodec.DefaultMaxSendLength, TimeSpan? operationTimeout = null)
        : this(stream, DnstapStreamMode.File, leaveOpen, maxDataLength, operationTimeout) { }

    /// <summary>Creates a file or duplex writer. Duplex operations have a finite per-I/O timeout (30 seconds by default).</summary>
    public DnstapWriter(Stream stream, DnstapStreamMode mode, bool leaveOpen = true,
        int maxDataLength = FrameCodec.DefaultMaxSendLength, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!stream.CanWrite || (mode == DnstapStreamMode.Duplex && !stream.CanRead))
            throw new ArgumentException("The stream must be writable, and duplex streams must also be readable.", nameof(stream));

        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _stream = stream;
        _mode = mode;
        _leaveOpen = leaveOpen;
        // Stream ownership stays here so even a failed cleanup lock releases an owned transport.
        _frames = new FrameSequenceWriter(stream, leaveOpen: true, maxDataLength,
            mode == DnstapStreamMode.Duplex ? _operationTimeout : null);
        _maxDataLength = maxDataLength;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // A pre-cancelled attempt never starts negotiation or changes writer state.
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _startAttempted, 1) != 0)
            throw new InvalidOperationException("Writer has already started.");
        try
        {
            if (_mode == DnstapStreamMode.Duplex)
            {
                var frame = new ArrayBufferWriter<byte>();
                FrameCodec.WriteControl(frame,
                    new ControlFrame(ControlTypes.Ready,
                        new ReadOnlyMemory<byte>[] { ControlTypes.DnstapContentType.ToArray() }));
                await StreamDeadline.WriteAsync(_stream, frame.WrittenMemory, _operationTimeout,
                    cancellationToken, "Frame Streams control write").ConfigureAwait(false);
                ControlFrame accepted = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
                if (accepted.Type != ControlTypes.Accept ||
                    (accepted.ContentTypes.Count != 0 &&
                     !accepted.ContentTypes.Any(type => type.Span.SequenceEqual(ControlTypes.DnstapContentType))))
                    throw new InvalidDataException("Collector did not accept the dnstap content type.");
            }

            await _frames.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Writes one initialized protobuf record. Concurrent calls are serialized; call ordering is not guaranteed.</summary>
    public Task WriteAsync(Dnstap message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_faulted)
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
        return _frames.WriteAsync(message.ToByteArray(), cancellationToken);
    }

    /// <summary>Sends STOP and, in duplex mode, waits for FINISH. A failed FINISH is terminal.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await WaitForCompletionGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_faulted)
                throw new InvalidOperationException("Writer is faulted.");
            if (_finished)
                return;
            if (!_started)
                throw new InvalidOperationException("Writer must be started before completion.");
            await CompleteStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _completionGate.Release();
        }
    }

    private async Task CompleteStartedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _frames.CompleteAsync(cancellationToken).ConfigureAwait(false);
            if (_mode == DnstapStreamMode.Duplex)
            {
                ControlFrame finished = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
                if (finished.Type != ControlTypes.Finish)
                    throw new InvalidDataException("Expected FINISH after STOP.");
            }
            _finished = true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private async Task<ControlFrame> ReadControlAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        try
        {
            await StreamDeadline.ReadExactlyAsync(_stream, header, _operationTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0)
                throw new InvalidDataException("Expected a Frame Streams control frame.");

            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (length < 4 || length > FrameCodec.MaxControlLength)
                throw new InvalidDataException("Invalid Frame Streams control length.");

            byte[] payload = new byte[(int)length];
            await StreamDeadline.ReadExactlyAsync(_stream, payload, _operationTimeout, cancellationToken)
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

    private async Task WaitForCompletionGateAsync(CancellationToken cancellationToken)
    {
        if (!await _completionGate.WaitAsync(_mode == DnstapStreamMode.Duplex
                ? _operationTimeout : Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("Timed out waiting for writer completion.");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DnstapWriter));
    }

    /// <summary>Completes healthy duplex sessions and always releases an owned transport, even on failure.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        bool acquired = false;
        try
        {
            await WaitForCompletionGateAsync(CancellationToken.None).ConfigureAwait(false);
            acquired = true;
            if (_mode == DnstapStreamMode.Duplex && _started && !_finished && !_faulted && !_frames.IsFaulted)
                await CompleteStartedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (acquired)
                    await _frames.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (acquired)
                    _completionGate.Release();
                if (!_leaveOpen)
                    await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
