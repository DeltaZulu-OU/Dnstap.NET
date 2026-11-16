using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using Dnstap.Protocol;

namespace Dnstap.Tests.Protocol
{

internal static class FileFrameSession
{
    private const int InitialBufferSize = 8192;

    // Check arguments before returning the iterator rather than during its first MoveNextAsync.
    // The incoming stream stays caller-owned; emitted payloads never borrow pooled storage.
    internal static IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        Stream stream,
        int maxDataLength = TestFrameCodec.DefaultMaxReceiveLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDataLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDataLength, int.MaxValue - 8);

        return ReadFramesAsync(stream, maxDataLength, cancellationToken);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(
        Stream stream,
        int maxDataLength,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new FrameReader(stream, maxDataLength);
        while (true)
        {
            FrameReadResult result = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            if (result.Stopped)
                yield break;

            if (result.Payload is not null)
                yield return result.Payload;
        }
    }

    private readonly record struct FrameReadResult(byte[]? Payload, bool Stopped = false);

    private sealed class FrameReader(Stream stream, int maxDataLength) : IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        private int _start;
        private int _buffered;
        private bool _started;

        public async Task<FrameReadResult> ReadNextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sequence = new ReadOnlySequence<byte>(_buffer.AsMemory(_start, _buffered - _start));
                OperationStatus status = TestFrameCodec.TryRead(
                    sequence, out FrameSlice frame, out long consumed, maxDataLength);
                if (status == OperationStatus.InvalidData)
                    throw new InvalidDataException("Malformed Frame Streams frame.");

                if (status != OperationStatus.Done)
                {
                    await ReadMoreAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                FrameReadResult result = ProcessFrame(frame);
                Advance(checked((int)consumed));
                if (result.Stopped)
                {
                    await VerifyStopAsync(cancellationToken).ConfigureAwait(false);
                    return result;
                }

                if (result.Payload is not null)
                    return result;
            }
        }

        private FrameReadResult ProcessFrame(FrameSlice frame)
        {
            if (frame.Kind == FrameKind.Control)
                return ProcessControl(frame.Payload);

            if (!_started)
                throw new InvalidDataException("Data frame received before START.");

            return new FrameReadResult(frame.Payload.ToArray());
        }

        private FrameReadResult ProcessControl(ReadOnlySequence<byte> payload)
        {
            if (ControlCodec.TryDecode(payload, out ControlFrame? control) != OperationStatus.Done)
                throw new InvalidDataException("Malformed control frame.");

            uint type = control!.Type;
            if (!_started)
            {
                if (type != ControlTypes.Start)
                    throw new InvalidDataException("File must begin with START.");
                if (control.ContentTypes.Count == 1
                    && !control.ContentTypes[0].Span.SequenceEqual(ControlTypes.DnstapContentType))
                    throw new InvalidDataException("Unexpected dnstap content type.");
                _started = true;
                return default;
            }

            if (type == ControlTypes.Stop)
                return new FrameReadResult(null, Stopped: true);

            if (type is ControlTypes.Accept or ControlTypes.Ready or ControlTypes.Start or ControlTypes.Finish)
                throw new InvalidDataException("Unexpected control frame during file session.");

            // Unknown numeric controls are ignored only after START.
            return default;
        }

        private void Advance(int consumed)
        {
            _start += consumed;
            if (_start != _buffered)
                return;

            _start = 0;
            _buffered = 0;
            // Release a megabyte-sized rental as soon as it is drained.
            if (_buffer.Length > InitialBufferSize)
                ReplaceBuffer(InitialBufferSize, copyStart: 0, count: 0);
        }

        private async Task VerifyStopAsync(CancellationToken cancellationToken)
        {
            if (_buffered != _start)
                throw new InvalidDataException("Trailing bytes after STOP.");

            cancellationToken.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(_buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read != 0)
                throw new InvalidDataException("Trailing bytes after STOP.");
        }

        private async Task ReadMoreAsync(CancellationToken cancellationToken)
        {
            Compact();
            int needed = GetRequiredBufferSize();
            int maximum = Math.Max(512 + 8, maxDataLength + 4);
            if (needed > maximum)
                throw new InvalidDataException("Frame cannot fit within the configured maximum.");

            if (needed > _buffer.Length)
                ReplaceBuffer(needed, copyStart: 0, count: _buffered);

            int count = await stream.ReadAsync(_buffer.AsMemory(_buffered), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new InvalidDataException("Unexpected EOF before STOP or inside a frame.");
            _buffered += count;
        }

        private void Compact()
        {
            if (_start == 0)
                return;

            int remaining = _buffered - _start;
            if (_buffer.Length > InitialBufferSize && remaining <= InitialBufferSize)
                ReplaceBuffer(InitialBufferSize, copyStart: _start, count: remaining);
            else if (remaining > 0)
                _buffer.AsSpan(_start, remaining).CopyTo(_buffer);

            _start = 0;
            _buffered = remaining;
        }

        private int GetRequiredBufferSize()
        {
            if (_buffered < 4)
                return _buffered + 1;

            uint declared = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(0, 4));
            if (declared != 0)
                return checked((int)declared + 4);

            return _buffered >= 8
                ? checked((int)BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(4, 4)) + 8)
                : 8;
        }

        private void ReplaceBuffer(int size, int copyStart, int count)
        {
            // Rent before returning the old buffer; a failed rental does not lose ownership.
            byte[] replacement = ArrayPool<byte>.Shared.Rent(size);
            _buffer.AsSpan(copyStart, count).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = replacement;
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
}
