using System.Buffers;
using System.Buffers.Binary;

namespace Dnstap.Protocol;

internal enum FrameKind { Data, Control }

/// <summary>A borrowed view of a test input buffer.</summary>
internal readonly struct FrameSlice(FrameKind kind, ReadOnlySequence<byte> payload)
{
    internal FrameKind Kind { get; } = kind;
    internal ReadOnlySequence<byte> Payload { get; } = payload;
}

internal static class TestFrameCodec
{
    internal const int DefaultMaxReceiveLength = 1_048_576;

    internal static OperationStatus TryRead(
        ReadOnlySequence<byte> input,
        out FrameSlice frame,
        out long consumed,
        int maxDataLength = DefaultMaxReceiveLength)
    {
        frame = default;
        consumed = 0;
        if (maxDataLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDataLength));

        if (input.Length < 4)
            return OperationStatus.NeedMoreData;

        Span<byte> header = stackalloc byte[4];
        input.Slice(0, 4).CopyTo(header);
        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header);

        if (payloadLength != 0)
        {
            if (payloadLength > (uint)maxDataLength)
                return OperationStatus.InvalidData;
            if (input.Length - 4 < payloadLength)
                return OperationStatus.NeedMoreData;
            consumed = 4 + payloadLength;
            frame = new FrameSlice(FrameKind.Data, input.Slice(4, payloadLength));
            return OperationStatus.Done;
        }

        if (input.Length < 8)
            return OperationStatus.NeedMoreData;
        input.Slice(4, 4).CopyTo(header);
        uint controlLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (controlLength < 4 || controlLength > 512)
            return OperationStatus.InvalidData;
        if (input.Length - 8 < controlLength)
            return OperationStatus.NeedMoreData;

        consumed = 8 + controlLength;
        frame = new FrameSlice(FrameKind.Control, input.Slice(8, controlLength));
        return OperationStatus.Done;
    }
}
