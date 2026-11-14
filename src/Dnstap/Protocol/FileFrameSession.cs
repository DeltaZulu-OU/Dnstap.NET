using System.Buffers;
using System.Runtime.CompilerServices;

namespace Dnstap.Protocol;

internal static class FileFrameSession
{
    // The caller owns the incoming stream. Returned data is caller-owned, not a borrowed pooled buffer.
    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int maxDataLength = FrameCodec.DefaultMaxReceiveLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxDataLength < 1 || maxDataLength > int.MaxValue - 8)
            throw new ArgumentOutOfRangeException(nameof(maxDataLength));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        int buffered = 0;
        bool started = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sequence = new ReadOnlySequence<byte>(buffer.AsMemory(0, buffered));
                OperationStatus result = FrameCodec.TryRead(sequence, out FrameSlice frame, out long consumed, maxDataLength);
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

                    int used = checked((int)consumed);
                    int remaining = buffered - used;
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

                    if (remaining > 0)
                        Array.Copy(buffer, used, buffer, 0, remaining);
                    buffered = remaining;
                    if (payload is not null)
                        yield return payload;
                    continue;
                }

                // NeedMoreData: grow only after FrameCodec has validated the declared length.
                if (buffered == buffer.Length)
                {
                    int maximum = Math.Max(FrameCodec.MaxControlLength + 8, maxDataLength + 4);
                    if (buffered >= maximum)
                        throw new InvalidDataException("Frame cannot fit within the configured maximum.");
                    int capacity = (int)Math.Min(maximum, Math.Max((long)buffer.Length + 1, (long)buffer.Length * 2));
                    byte[] grown = ArrayPool<byte>.Shared.Rent(capacity);
                    Array.Copy(buffer, grown, buffered);
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

internal sealed class FileFrameWriter(Stream stream, bool leaveOpen = true) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _outgoing = new();
    private bool _started;
    private bool _completed;
    private bool _faulted;
    private bool _disposed;

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (_started)
                throw new InvalidOperationException("Already started.");
            try
            {
                _outgoing.Clear();
                FrameCodec.WriteControl(_outgoing,
                    new ControlFrame(ControlTypes.Start,
                        new ReadOnlyMemory<byte>[] { ControlTypes.DnstapContentType.ToArray() }));
                await stream.WriteAsync(_outgoing.WrittenMemory, cancellationToken).ConfigureAwait(false);
                _started = true;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (!_started || _completed)
                throw new InvalidOperationException("Writer has not started or is completed.");
            _outgoing.Clear();
            FrameCodec.WriteData(_outgoing, data.Span);
            try
            {
                await stream.WriteAsync(_outgoing.WrittenMemory, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FileFrameWriter));
            await CompleteCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CompleteCoreAsync(CancellationToken cancellationToken)
    {
        if (_completed || _faulted)
            return;
        if (!_started)
        {
            _completed = true; // No stream was opened; do not write STOP without START.
            return;
        }
        try
        {
            _outgoing.Clear();
            FrameCodec.WriteControl(_outgoing, new ControlFrame(ControlTypes.Stop));
            await stream.WriteAsync(_outgoing.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void EnsureUsable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FileFrameWriter));
        if (_faulted)
            throw new InvalidOperationException("Writer is faulted after an earlier I/O error.");
        if (_completed)
            throw new InvalidOperationException("Writer is completed.");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await CompleteCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (!leaveOpen)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
