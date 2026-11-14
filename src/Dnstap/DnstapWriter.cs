using System.Buffers;
using System.Buffers.Binary;
using Dnstap.Protocol;
using Google.Protobuf;

namespace Dnstap;

public enum DnstapStreamMode { File, Duplex }

/// <summary>Writes dnstap protobuf messages using the Frame Streams protocol.</summary>
public sealed class DnstapWriter : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly FileFrameWriter _frames;
    private readonly DnstapStreamMode _mode;
    private readonly int _maxDataLength;
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private int _startAttempted;
    private int _disposed;
    private bool _finished;

    public DnstapWriter(Stream stream, bool leaveOpen = true, int maxDataLength = 96 * 1024)
        : this(stream, DnstapStreamMode.File, leaveOpen, maxDataLength) { }

    public DnstapWriter(Stream stream, DnstapStreamMode mode, bool leaveOpen = true, int maxDataLength = 96 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == DnstapStreamMode.Duplex && (!stream.CanRead || !stream.CanWrite))
            throw new ArgumentException("Duplex mode requires a readable and writable stream.", nameof(stream));

        _stream = stream;
        _mode = mode;
        _frames = new FileFrameWriter(stream, leaveOpen, maxDataLength);
        _maxDataLength = maxDataLength;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _startAttempted, 1) != 0)
            throw new InvalidOperationException("Writer has already started.");

        if (_mode == DnstapStreamMode.Duplex)
        {
            var frame = new ArrayBufferWriter<byte>();
            FrameCodec.WriteControl(frame,
                new ControlFrame(ControlTypes.Ready,
                    new ReadOnlyMemory<byte>[] { ControlTypes.DnstapContentType.ToArray() }));
            await _stream.WriteAsync(frame.WrittenMemory, cancellationToken).ConfigureAwait(false);
            ControlFrame accepted = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
            if (accepted.Type != ControlTypes.Accept ||
                (accepted.ContentTypes.Count != 0 &&
                 !accepted.ContentTypes.Any(type => type.Span.SequenceEqual(ControlTypes.DnstapContentType))))
                throw new InvalidDataException("Collector did not accept the dnstap content type.");
        }

        await _frames.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WriteAsync(Dnstap message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        if (!message.IsInitialized())
            throw new ArgumentException("dnstap required fields are missing.", nameof(message));
        int size = message.CalculateSize();
        if (size < 1 || size > _maxDataLength)
            throw new ArgumentOutOfRangeException(nameof(message), "dnstap message exceeds the configured frame size.");
        return _frames.WriteAsync(message.ToByteArray(), cancellationToken);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _completionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_finished)
                return;

            await _frames.CompleteAsync(cancellationToken).ConfigureAwait(false);
            if (_mode == DnstapStreamMode.Duplex)
            {
                ControlFrame finished = await ReadControlAsync(cancellationToken).ConfigureAwait(false);
                if (finished.Type != ControlTypes.Finish)
                    throw new InvalidDataException("Expected FINISH after STOP.");
            }
            _finished = true;
        }
        finally
        {
            _completionGate.Release();
        }
    }

    private async Task<ControlFrame> ReadControlAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        await _stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0)
            throw new InvalidDataException("Expected a Frame Streams control frame.");

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (length < 4 || length > FrameCodec.MaxControlLength)
            throw new InvalidDataException("Invalid Frame Streams control length.");

        byte[] payload = new byte[(int)length];
        await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        if (ControlCodec.TryDecode(new ReadOnlySequence<byte>(payload), out ControlFrame? control) != OperationStatus.Done)
            throw new InvalidDataException("Invalid Frame Streams control payload.");
        return control!;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DnstapWriter));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return _frames.DisposeAsync();
    }
}
