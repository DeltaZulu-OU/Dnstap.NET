using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Dnstap.Protocol;

namespace Dnstap.Tests.Protocol
{
    internal static class FileFrameSession
    {
        /// <summary>
        /// The caller owns the incoming stream. Returned data is caller-owned, not a borrowed pooled buffer.
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="maxDataLength"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        /// <exception cref="InvalidDataException"></exception>
        internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
            Stream stream,
            int maxDataLength = TestFrameCodec.DefaultMaxReceiveLength,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (maxDataLength is < 1 or > (int.MaxValue - 8))
            {
                throw new ArgumentOutOfRangeException(nameof(maxDataLength));
            }

            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            var buffered = 0;
            var start = 0;
            var started = false;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sequence = new ReadOnlySequence<byte>(buffer.AsMemory(start, buffered - start));
                    var result = TestFrameCodec.TryRead(sequence, out var frame, out var consumed, maxDataLength);
                    if (result == OperationStatus.InvalidData)
                    {
                        throw new InvalidDataException("Malformed Frame Streams frame.");
                    }

                    if (result == OperationStatus.Done)
                    {
                        byte[]? payload = null;
                        var stop = false;
                        if (frame.Kind == FrameKind.Control)
                        {
                            if (ControlCodec.TryDecode(frame.Payload, out var control) != OperationStatus.Done)
                            {
                                throw new InvalidDataException("Malformed control frame.");
                            }

                            var type = control!.Type;
                            if (!started)
                            {
                                if (type != ControlTypes.Start)
                                {
                                    throw new InvalidDataException("File must begin with START.");
                                }

                                if (control.ContentTypes.Count == 1
                                    && !control.ContentTypes[0].Span.SequenceEqual(ControlTypes.DnstapContentType))
                                {
                                    throw new InvalidDataException("Unexpected dnstap content type.");
                                }

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
                            {
                                throw new InvalidDataException("Data frame received before START.");
                            }

                            payload = frame.Payload.ToArray();
                        }

                        start += checked((int)consumed);
                        var remaining = buffered - start;
                        if (stop)
                        {
                            if (remaining != 0)
                            {
                                throw new InvalidDataException("Trailing bytes after STOP.");
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                                .ConfigureAwait(false);
                            if (read != 0)
                            {
                                throw new InvalidDataException("Trailing bytes after STOP.");
                            }

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
                                var smaller = ArrayPool<byte>.Shared.Rent(8192);
                                ArrayPool<byte>.Shared.Return(buffer);
                                buffer = smaller;
                            }
                        }
                        // Otherwise keep [start, buffered) as a borrowed slice.
                        // Do not move bytes once for each frame.
                        if (payload is not null)
                        {
                            yield return payload;
                        }

                        continue;
                    }

                    // NeedMoreData: compact at most once per stream read, never per frame.
                    // After a large frame, switch back to the normal buffer if the
                    // incomplete tail is small enough.
                    if (start > 0)
                    {
                        var remaining = buffered - start;
                        if (buffer.Length > 8192 && remaining <= 8192)
                        {
                            var smaller = ArrayPool<byte>.Shared.Rent(8192);
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
                    var needed = buffered + 1;
                    if (buffered >= 4)
                    {
                        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
#pragma warning disable IDE0045 // Convert to conditional expression
                        if (declaredLength != 0)
                        {
                            needed = checked((int)declaredLength + 4);
                        }
                        else if (buffered >= 8)
                        {
                            needed = checked((int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4)) + 8);
                        }
                        else
                        {
                            needed = 8;
                        }
#pragma warning restore IDE0045 // Convert to conditional expression
                    }

                    var maximum = Math.Max(512 + 8, maxDataLength + 4);
                    if (needed > maximum)
                    {
                        throw new InvalidDataException("Frame cannot fit within the configured maximum.");
                    }

                    if (needed > buffer.Length)
                    {
                        var grown = ArrayPool<byte>.Shared.Rent(needed);
                        buffer.AsSpan(0, buffered).CopyTo(grown);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = grown;
                    }

                    var count = await stream.ReadAsync(buffer.AsMemory(buffered), cancellationToken)
                        .ConfigureAwait(false);
                    if (count == 0)
                    {
                        throw new InvalidDataException("Unexpected EOF before STOP or inside a frame.");
                    }

                    buffered += count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
