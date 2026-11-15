using System.Buffers;
using System.Buffers.Binary;

namespace Dnstap.Tests.Protocol
{
    internal enum FrameKind { Data = 0, Control = 1 }

    /// <summary>A borrowed view of a test input buffer.</summary>
    /// <param name="kind"></param>
    /// <param name="payload"></param>
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
            ArgumentOutOfRangeException.ThrowIfLessThan(maxDataLength, 1);

            if (input.Length < 4)
            {
                return OperationStatus.NeedMoreData;
            }

            Span<byte> header = stackalloc byte[4];
            input.Slice(0, 4).CopyTo(header);
            var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header);

            if (payloadLength != 0)
            {
                if (payloadLength > (uint)maxDataLength)
                {
                    return OperationStatus.InvalidData;
                }

                if (input.Length - 4 < payloadLength)
                {
                    return OperationStatus.NeedMoreData;
                }

                consumed = 4 + payloadLength;
                frame = new FrameSlice(FrameKind.Data, input.Slice(4, payloadLength));
                return OperationStatus.Done;
            }

            if (input.Length < 8)
            {
                return OperationStatus.NeedMoreData;
            }

            input.Slice(4, 4).CopyTo(header);
            var controlLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (controlLength is < 4 or > 512)
            {
                return OperationStatus.InvalidData;
            }

            if (input.Length - 8 < controlLength)
            {
                return OperationStatus.NeedMoreData;
            }

            consumed = 8 + controlLength;
            frame = new FrameSlice(FrameKind.Control, input.Slice(8, controlLength));
            return OperationStatus.Done;
        }
    }
}
