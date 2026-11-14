using System.Buffers;
using System.Buffers.Binary;

namespace Dnstap.Protocol;

internal static class ControlTypes
{
    internal const uint Accept = 1;
    internal const uint Start = 2;
    internal const uint Stop = 3;
    internal const uint Ready = 4;
    internal const uint Finish = 5;
    internal const uint ContentTypeField = 1;
    internal static ReadOnlySpan<byte> DnstapContentType => "protobuf:dnstap.Dnstap"u8;
}

/// <summary>Binary-safe control metadata; byte-array field values are not implicitly comparable records.</summary>
internal sealed class ControlFrame(uint type, IReadOnlyList<ReadOnlyMemory<byte>>? contentTypes = null)
{
    internal uint Type { get; } = type;
    internal IReadOnlyList<ReadOnlyMemory<byte>> ContentTypes { get; } =
        contentTypes ?? Array.Empty<ReadOnlyMemory<byte>>();
}

internal static class ControlCodec
{
    internal static OperationStatus TryDecode(ReadOnlySequence<byte> payload, out ControlFrame? control)
    {
        control = null;
        if (payload.Length < 4 || payload.Length > FrameCodec.MaxControlLength)
            return OperationStatus.InvalidData;

        // Control payload is limited to 512 bytes, including the four-byte type.
        byte[] bytes = payload.ToArray();
        uint type = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var fields = new List<ReadOnlyMemory<byte>>();
        int offset = 4;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8)
                return OperationStatus.InvalidData;

            uint fieldType = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            uint fieldLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4));
            offset += 8;

            if (fieldType != ControlTypes.ContentTypeField
                || fieldLength > FrameCodec.MaxContentTypeLength
                || fieldLength > bytes.Length - offset)
                return OperationStatus.InvalidData;

            fields.Add(bytes.AsMemory(offset, (int)fieldLength));
            offset += (int)fieldLength;
        }

        if (!IsLegalCardinality(type, fields.Count))
            return OperationStatus.InvalidData;

        control = new ControlFrame(type, fields);
        return OperationStatus.Done;
    }

    internal static int GetEncodedLength(ControlFrame control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!IsLegalCardinality(control.Type, control.ContentTypes.Count))
            throw new ArgumentException("The control type does not permit this number of content types.", nameof(control));

        long length = 4;
        foreach (ReadOnlyMemory<byte> field in control.ContentTypes)
        {
            if (field.Length > FrameCodec.MaxContentTypeLength)
                throw new ArgumentOutOfRangeException(nameof(control), "Content type is longer than 256 bytes.");
            length += 8L + field.Length;
        }
        if (length > FrameCodec.MaxControlLength)
            throw new ArgumentOutOfRangeException(nameof(control), "Control payload exceeds 512 bytes.");

        return (int)length;
    }

    internal static void Write(Span<byte> destination, ControlFrame control)
    {
        int length = GetEncodedLength(control);
        if (destination.Length < length)
            throw new ArgumentException("Output buffer is too small.", nameof(destination));

        BinaryPrimitives.WriteUInt32BigEndian(destination, control.Type);
        int offset = 4;
        foreach (ReadOnlyMemory<byte> field in control.ContentTypes)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], ControlTypes.ContentTypeField);
            BinaryPrimitives.WriteUInt32BigEndian(destination[(offset + 4)..], (uint)field.Length);
            offset += 8;
            field.Span.CopyTo(destination[offset..]);
            offset += field.Length;
        }
    }

    private static bool IsLegalCardinality(uint type, int count) =>
        type switch
        {
            ControlTypes.Start => count <= 1,
            ControlTypes.Stop or ControlTypes.Finish => count == 0,
            _ => true // READY/ACCEPT and future numeric control types
        };
}
