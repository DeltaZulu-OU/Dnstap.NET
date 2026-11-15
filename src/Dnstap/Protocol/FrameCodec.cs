using System.Buffers;
using System.Buffers.Binary;

namespace Dnstap.Protocol;

internal static class FrameCodec
{
    internal const int MaxControlLength = 512;
    internal const int MaxContentTypeLength = 256;
    internal const int DefaultMaxSendLength = 96 * 1024;

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
