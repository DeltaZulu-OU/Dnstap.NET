namespace Dnstap.Protocol;

// Contracts under test. The initial Red iteration intentionally has no codec implementation.
internal sealed record Frame(ReadOnlyMemory<byte> Data, ControlFrame? Control)
{
    internal bool IsControl => Control is not null;
}

internal sealed record ControlFrame(uint Type, IReadOnlyList<byte[]> ContentTypes);

internal static class FrameCodec
{
    internal const int MaxControlLength = 512;
    internal const int MaxContentTypeLength = 256;
    internal const int DefaultMaxReceiveLength = 1_048_576;
    internal const int DefaultMaxSendLength = 96 * 1024;

    internal static byte[] EncodeData(
        ReadOnlySpan<byte> payload,
        int maxPayloadLength = DefaultMaxSendLength) =>
        throw new NotImplementedException("Red phase: FS-001/FS-005 frame encoding.");

    internal static byte[] EncodeControl(ControlFrame frame) =>
        throw new NotImplementedException("Red phase: FS-001/FS-007 control encoding.");

    internal static Frame Decode(
        ReadOnlySpan<byte> frame,
        int maxDataLength = DefaultMaxReceiveLength) =>
        throw new NotImplementedException("Red phase: FS-001..FS-009 frame decoding.");
}

internal static class FileFrameSession
{
    internal static IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        Stream stream, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Red phase: FS-010/FS-011/FS-013 file reading.");

    internal static Task WriteAsync(
        Stream stream, IReadOnlyList<ReadOnlyMemory<byte>> payloads,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Red phase: FS-005/FS-011 file writing.");
}
