using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Dnstap.Protocol;

internal static class FileFrameSession
{
    // The caller owns the incoming stream. Returned data is caller-owned, not a borrowed pooled buffer.
    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int maxDataLength = TestFrameCodec.DefaultMaxReceiveLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxDataLength < 1 || maxDataLength > int.MaxValue - 8)
            throw new ArgumentOutOfRangeException(nameof(maxDataLength));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        int buffered = 0;
        int start = 0;
        bool started = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sequence = new ReadOnlySequence<byte>(buffer.AsMemory(start, buffered - start));
                OperationStatus result = TestFrameCodec.TryRead(sequence, out FrameSlice frame, out long consumed, maxDataLength);
                if (result == OperationStatus.InvalidData)
                    throw new InvalidDataException("Malformed Frame Streams frame.");

                if (result == OperationStatus.Done)
                {
                    byte[]? payload = null;
                    bool stop = false;
                    if (frame.Kind == FrameKind.Control)
                    {
                        if (ControlCodec.TryDecode(frame.Payload, out ControlFrame? control) != OperationStatus.Done)
                            throw new InvalidDataException("Malformed control frame.");
                        uint type = control!.Type;
                        if (!started)
                        {
                            if (type != ControlTypes.Start)
                                throw new InvalidDataException("File must begin with START.");
                            if (control.ContentTypes.Count == 1
                                && !control.ContentTypes[0].Span.SequenceEqual(ControlTypes.DnstapContentType))
                                throw new InvalidDataException("Unexpected dnstap content type.");
                            started = true;
                        }
                        else if (type == ControlTypes.Stop)
                        {
                            stop = true;
                        }
                        else if (type is ControlTypes.Accept or ControlTypes.Ready
                                 or ControlTypes.Start or ControlTypes.Finish)
                        {
                            throw new InvalidDataException("Unexpected control frame during file session.");
                        }
                        // Unknown numeric controls are ignored only after START.
                    }
                    else
                    {
                        if (!started)
                            throw new InvalidDataException("Data frame received before START.");
                        payload = frame.Payload.ToArray();
                    }

                    start += checked((int)consumed);
                    int remaining = buffered - start;
                    if (stop)
                    {
                        if (remaining != 0)
                            throw new InvalidDataException("Trailing bytes after STOP.");
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                            .ConfigureAwait(false);
                        if (read != 0)
                            throw new InvalidDataException("Trailing bytes after STOP.");
                        yield break;
                    }

                    if (remaining == 0)
                    {
                        start = 0;
                        buffered = 0;
                        // Release a megabyte-sized rental as soon as it is drained.
                        if (buffer.Length > 8192)
                        {
                            // Rent before returning the old buffer, so a failed
                            // rent cannot leave the same array owned twice.
                            byte[] smaller = ArrayPool<byte>.Shared.Rent(8192);
                            ArrayPool<byte>.Shared.Return(buffer);
                            buffer = smaller;
                        }
                    }
                    // Otherwise keep [start, buffered) as a borrowed slice.
                    // Do not move bytes once for each frame.
                    if (payload is not null)
                        yield return payload;
                    continue;
                }

                // NeedMoreData: compact at most once per stream read, never per frame.
                // After a large frame, switch back to the normal buffer if the
                // incomplete tail is small enough.
                if (start > 0)
                {
                    int remaining = buffered - start;
                    if (buffer.Length > 8192 && remaining <= 8192)
                    {
                        byte[] smaller = ArrayPool<byte>.Shared.Rent(8192);
                        buffer.AsSpan(start, remaining).CopyTo(smaller);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = smaller;
                    }
                    else if (remaining > 0)
                    {
                        buffer.AsSpan(start, remaining).CopyTo(buffer);
                    }

                    start = 0;
                    buffered = remaining;
                }

                // TryRead has already checked the declared length against the
                // configured limit. Size the rental from the complete frame
                // length as soon as its header is known, even when the current
                // buffer is not yet full after compaction.
                int needed = buffered + 1;
                if (buffered >= 4)
                {
                    uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
                    needed = declaredLength != 0
                        ? checked((int)declaredLength + 4)
                        : buffered >= 8
                            ? checked((int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4)) + 8)
                            : 8;
                }

                int maximum = Math.Max(FrameCodec.MaxControlLength + 8, maxDataLength + 4);
                if (needed > maximum)
                    throw new InvalidDataException("Frame cannot fit within the configured maximum.");
                if (needed > buffer.Length)
                {
                    byte[] grown = ArrayPool<byte>.Shared.Rent(needed);
                    buffer.AsSpan(0, buffered).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                int count = await stream.ReadAsync(buffer.AsMemory(buffered), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                    throw new InvalidDataException("Unexpected EOF before STOP or inside a frame.");
                buffered += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
