using System.Buffers;
using System.Buffers.Binary;

namespace Dnstap.Protocol;

internal sealed class FrameSequenceWriter(
    Stream stream,
    bool leaveOpen = true,
    int maxDataLength = FrameCodec.DefaultMaxSendLength,
    TimeSpan? operationTimeout = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _outgoing = new();
    private readonly byte[] _dataHeader = new byte[4];
    private readonly int _maxDataLength = maxDataLength > 0
        ? maxDataLength
        : throw new ArgumentOutOfRangeException(nameof(maxDataLength));
    private bool _started;
    private bool _completed;
    private bool _faulted;
    internal bool IsFaulted => Volatile.Read(ref _faulted);
    private bool _disposed;

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (_started)
                throw new InvalidOperationException("Already started.");
            try
            {
                _outgoing.ResetWrittenCount();
                FrameCodec.WriteControl(_outgoing,
                    new ControlFrame(ControlTypes.Start,
                        new ReadOnlyMemory<byte>[] { ControlTypes.DnstapContentType.ToArray() }));
                await WriteStreamAsync(_outgoing.WrittenMemory, cancellationToken).ConfigureAwait(false);
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
        await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (!_started || _completed)
                throw new InvalidOperationException("Writer has not started or is completed.");
            // Reject invalid payloads before writing even the four-byte header.
            if (data.IsEmpty || data.Length > _maxDataLength)
                throw new ArgumentOutOfRangeException(nameof(data));

            BinaryPrimitives.WriteUInt32BigEndian(_dataHeader, (uint)data.Length);
            try
            {
                // Writes are serialized by _gate. The caller's payload is not copied.
                await WriteStreamAsync(_dataHeader, cancellationToken).ConfigureAwait(false);
                await WriteStreamAsync(data, cancellationToken).ConfigureAwait(false);
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
        await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FrameSequenceWriter));
            if (_faulted)
                throw new InvalidOperationException("Cannot complete a faulted Frame Streams session.");
            if (!_started)
                throw new InvalidOperationException("Cannot complete a stream before START.");
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
            _outgoing.ResetWrittenCount();
            FrameCodec.WriteControl(_outgoing, new ControlFrame(ControlTypes.Stop));
            await WriteStreamAsync(_outgoing.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await FlushStreamAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private async Task AcquireAsync(CancellationToken cancellationToken)
    {
        if (operationTimeout is TimeSpan timeout)
        {
            if (!await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
                throw new TimeoutException("Timed out waiting for the Frame Streams write lock.");
        }
        else
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteStreamAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ValueTask write = stream.WriteAsync(payload, cancellationToken);
        if (operationTimeout is TimeSpan timeout)
            await write.AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        else
            await write.ConfigureAwait(false);
    }

    private async Task FlushStreamAsync(CancellationToken cancellationToken)
    {
        Task flush = stream.FlushAsync(cancellationToken);
        if (operationTimeout is TimeSpan timeout)
            await flush.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        else
            await flush.ConfigureAwait(false);
    }

    private void EnsureUsable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FrameSequenceWriter));
        if (_faulted)
            throw new InvalidOperationException("Writer is faulted after an earlier I/O error.");
        if (_completed)
            throw new InvalidOperationException("Writer is completed.");
    }

    public async ValueTask DisposeAsync()
    {
        await AcquireAsync(CancellationToken.None).ConfigureAwait(false);
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
