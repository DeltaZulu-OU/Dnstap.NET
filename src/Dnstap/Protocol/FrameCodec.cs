using System.Buffers;
using System.Buffers.Binary;

namespace Dnstap.Protocol;

internal enum FrameKind { Data, Control }

/// <summary>Borrowed view into the caller-owned sequence. Do not retain after consuming the sequence.</summary>
internal readonly struct FrameSlice(FrameKind kind, ReadOnlySequence<byte> payload)
{
    internal FrameKind Kind { get; } = kind;
    internal ReadOnlySequence<byte> Payload { get; } = payload;
}

internal static class FrameCodec
{
    internal const int MaxControlLength = 512;
    internal const int MaxContentTypeLength = 256;
    internal const int DefaultMaxReceiveLength = 1_048_576;
    internal const int DefaultMaxSendLength = 96 * 1024;

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
        if (controlLength < 4 || controlLength > MaxControlLength)
            return OperationStatus.InvalidData;
        if (input.Length - 8 < controlLength)
            return OperationStatus.NeedMoreData;

        consumed = 8 + controlLength;
        frame = new FrameSlice(FrameKind.Control, input.Slice(8, controlLength));
        return OperationStatus.Done;
    }

    internal static void WriteData(
        IBufferWriter<byte> output,
        ReadOnlySpan<byte> data,
        int maxDataLength = DefaultMaxSendLength)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (maxDataLength <= 0 || data.Length == 0 || data.Length > maxDataLength)
            throw new ArgumentOutOfRangeException(nameof(data));
        Span<byte> span = output.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)data.Length);
        output.Advance(4);
        output.Write(data);
    }

    internal static void WriteControl(IBufferWriter<byte> output, ControlFrame control)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(control);
        int payloadLength = ControlCodec.GetEncodedLength(control);
        Span<byte> span = output.GetSpan(payloadLength + 8);
        BinaryPrimitives.WriteUInt32BigEndian(span, 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)payloadLength);
        ControlCodec.Write(span[8..], control);
        output.Advance(payloadLength + 8);
    }
}
