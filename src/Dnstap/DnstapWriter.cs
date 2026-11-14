using Google.Protobuf;
using Dnstap.Protocol;

namespace Dnstap;

/// <summary>Writes schema-generated dnstap records as a Frame Streams file.</summary>
public sealed class DnstapWriter : IAsyncDisposable
{
    private readonly FileFrameWriter _frames;
    private readonly int _maxDataLength;

    public DnstapWriter(Stream stream, bool leaveOpen = true, int maxDataLength = 96 * 1024)
    {
        _frames = new FileFrameWriter(stream, leaveOpen, maxDataLength);
        _maxDataLength = maxDataLength;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _frames.StartAsync(cancellationToken);

    public Task WriteAsync(Dnstap message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.IsInitialized())
            throw new ArgumentException("dnstap required fields are missing.", nameof(message));
        int size = message.CalculateSize();
        if (size < 1 || size > _maxDataLength)
            throw new ArgumentOutOfRangeException(nameof(message), "dnstap message exceeds the configured frame size.");
        return _frames.WriteAsync(message.ToByteArray(), cancellationToken);
    }

    public Task CompleteAsync(CancellationToken cancellationToken = default) =>
        _frames.CompleteAsync(cancellationToken);

    public ValueTask DisposeAsync() => _frames.DisposeAsync();
}
