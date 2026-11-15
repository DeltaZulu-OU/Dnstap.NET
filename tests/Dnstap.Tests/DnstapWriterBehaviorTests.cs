using Dnstap.Protocol;
using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class DnstapWriterBehaviorTests
{
    [TestMethod]
    public void OfficialProtobufMessageRoundTripsWithoutChangingPacketBytes()
    {
        global::Dnstap.Dnstap expected = Sample();
        byte[] bytes = expected.ToByteArray();
        global::Dnstap.Dnstap actual = global::Dnstap.Dnstap.Parser.ParseFrom(bytes);

        Assert.IsTrue(actual.IsInitialized());
        Assert.AreEqual(expected.Type, actual.Type);
        Assert.AreEqual(expected.Identity, actual.Identity);
        Assert.AreEqual(expected.Message.Type, actual.Message.Type);
        Assert.AreEqual(expected.Message.SocketProtocol, actual.Message.SocketProtocol);
        Assert.AreEqual(expected.Message.QueryPort, actual.Message.QueryPort);
        Assert.AreEqual(expected.Message.QueryTimeSec, actual.Message.QueryTimeSec);
        Assert.AreEqual(expected.Message.QueryTimeNsec, actual.Message.QueryTimeNsec);
        Assert.AreEqual(expected.Message.QueryAddress, actual.Message.QueryAddress);
        Assert.AreEqual(expected.Message.QueryMessage, actual.Message.QueryMessage);
    }

    [TestMethod]
    public async Task FileWriterProducesValidFramesContainingProtobufMessages()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new DnstapWriter(stream))
        {
            await writer.StartAsync();
            await writer.WriteAsync(Sample());
            await writer.CompleteAsync();
        }

        stream.Position = 0;
        var records = new List<global::Dnstap.Dnstap>();
        await foreach (ReadOnlyMemory<byte> payload in FileFrameSession.ReadAsync(stream))
            records.Add(global::Dnstap.Dnstap.Parser.ParseFrom(payload.ToArray()));

        Assert.HasCount(1, records);
        Assert.IsTrue(records[0].IsInitialized());
        CollectionAssert.AreEqual(Sample().Message.QueryMessage.ToByteArray(), records[0].Message.QueryMessage.ToByteArray());
    }

    [TestMethod]
    public async Task MissingRequiredProtobufFieldsDoNotWriteAnyDataFrame()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new DnstapWriter(stream))
        {
            await writer.StartAsync();
            long before = stream.Length;
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => writer.WriteAsync(new global::Dnstap.Dnstap()));
            Assert.AreEqual(before, stream.Length);
            await writer.CompleteAsync();
        }
        stream.Position = 0;
        int count = 0;
        await foreach (ReadOnlyMemory<byte> ignored in FileFrameSession.ReadAsync(stream))
            count++;
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public void FileWriterRejectsReadOnlyStreamAtConstruction()
    {
        using var stream = new MemoryStream(new byte[8], writable: false);
        Assert.ThrowsExactly<ArgumentException>(() => new DnstapWriter(stream));
    }

    [TestMethod]
    public void FrameLengthLimitRejectsSizesThatCannotFitManagedFrameBuffer()
    {
        using var stream = new MemoryStream();
        int maximum = Array.MaxLength - sizeof(uint);

        // Boundary validation must not itself allocate a large frame.
        var file = new DnstapWriter(stream, maxDataLength: maximum);
        var duplex = new DnstapWriter(stream, DnstapStreamMode.Duplex, maxDataLength: maximum);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DnstapWriter(stream, maxDataLength: maximum + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new DnstapWriter(stream, DnstapStreamMode.Duplex, maxDataLength: int.MaxValue));

        file.DisposeAsync().AsTask().GetAwaiter().GetResult();
        duplex.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task PreCancelledStartDoesNotPreventRetry()
    {
        await using var stream = new MemoryStream();
        await using var writer = new DnstapWriter(stream);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => writer.StartAsync(cancellation.Token));
        await writer.StartAsync();
        await writer.CompleteAsync();
        stream.Position = 0;
        int count = 0;
        await foreach (var _ in FileFrameSession.ReadAsync(stream))
            count++;
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task NestedMissingRequiredTypeIsRejectedBeforeDataWrite()
    {
        await using var stream = new MemoryStream();
        await using var writer = new DnstapWriter(stream);
        await writer.StartAsync();
        long before = stream.Length;
        var incomplete = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message()
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => writer.WriteAsync(incomplete));
        Assert.AreEqual(before, stream.Length);
        await writer.CompleteAsync();
    }

    [TestMethod]
    public async Task ExactMessageLimitIsAcceptedAndLargerMessageRejectedWithoutWriting()
    {
        var sample = Sample();
        await using var stream = new MemoryStream();
        await using var writer = new DnstapWriter(stream, maxDataLength: sample.CalculateSize());
        await writer.StartAsync();
        await writer.WriteAsync(sample);
        long before = stream.Length;
        sample.Identity = ByteString.CopyFromUtf8("a much longer test identity");
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => writer.WriteAsync(sample));
        Assert.AreEqual(before, stream.Length);
        await writer.CompleteAsync();
    }


    [TestMethod]
    public async Task UnknownRequiredEnumValuesAreRejectedBeforeWritingData()
    {
        await using var stream = new MemoryStream();
        await using var writer = new DnstapWriter(stream);
        await writer.StartAsync();
        long before = stream.Length;

        var unknownEnvelope = Sample();
        unknownEnvelope.Type = (global::Dnstap.Dnstap.Types.Type)99;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => writer.WriteAsync(unknownEnvelope));
        Assert.AreEqual(before, stream.Length);

        var unknownMessage = Sample();
        unknownMessage.Message.Type = (global::Dnstap.Message.Types.Type)99;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => writer.WriteAsync(unknownMessage));
        Assert.AreEqual(before, stream.Length);
        await writer.CompleteAsync();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void InvalidPublicSendLimitIsRejected(int limit)
    {
        using var stream = new MemoryStream();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DnstapWriter(stream, maxDataLength: limit));
    }


    [TestMethod]
    public async Task ConcurrentPublicWritesProduceCompleteNonInterleavedRecords()
    {
        await using var stream = new YieldingWriteStream();
        await using (var writer = new DnstapWriter(stream))
        {
            await writer.StartAsync();
            Task[] writes = Enumerable.Range(0, 64).Select(i =>
            {
                var message = Sample();
                message.Message.QueryTimeNsec = (uint)i;
                return writer.WriteAsync(message);
            }).ToArray();
            await Task.WhenAll(writes);
            await writer.CompleteAsync();
        }

        Assert.IsFalse(stream.Overlapped, "Writes must not overlap even when the transport yields.");
        await using var capture = new MemoryStream(stream.ToArray());
        var seen = new HashSet<uint>();
        await foreach (var payload in FileFrameSession.ReadAsync(capture))
        {
            var decoded = global::Dnstap.Dnstap.Parser.ParseFrom(payload.ToArray());
            Assert.IsTrue(decoded.IsInitialized());
            Assert.IsTrue(seen.Add(decoded.Message.QueryTimeNsec));
        }
        Assert.AreEqual(64, seen.Count);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 64).Select(i => (uint)i).ToArray(), seen.ToArray());
    }


    [TestMethod]
    public async Task CancelledWriteWaitingForLockLeavesWriterUsable()
    {
        await using var stream = new YieldingWriteStream(blockFirstDataWrite: true);
        await using var writer = new DnstapWriter(stream);
        await writer.StartAsync();
        Task first = writer.WriteAsync(Sample());
        await stream.FirstDataWriteStarted.WaitAsync(TimeSpan.FromSeconds(3));

        using var cancel = new CancellationTokenSource();
        Task blocked = writer.WriteAsync(Sample(), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => blocked);
        stream.ReleaseBlockedWrite();
        await first;
        await writer.WriteAsync(Sample());
        await writer.CompleteAsync();

        await using var input = new MemoryStream(stream.ToArray());
        int count = 0;
        await foreach (var _ in FileFrameSession.ReadAsync(input))
            count++;
        Assert.AreEqual(2, count);
        Assert.IsFalse(stream.Overlapped);
    }

    [TestMethod]
    public async Task EnvelopeWithoutNestedMessageIsRejectedWithoutWriting()
    {
        await using var stream = new MemoryStream();
        await using var writer = new DnstapWriter(stream);
        await writer.StartAsync();
        long before = stream.Length;
        var emptyEnvelope = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => writer.WriteAsync(emptyEnvelope));
        Assert.AreEqual(before, stream.Length);
        await writer.CompleteAsync();
    }


    [TestMethod]
    public async Task CancellingFileWriteMidFrameFaultsWriterWithoutAppendingStop()
    {
        await using var stream = new YieldingWriteStream(blockFirstDataWrite: true);
        var writer = new DnstapWriter(stream);
        await writer.StartAsync();
        using var cancellation = new CancellationTokenSource();
        Task write = writer.WriteAsync(Sample(), cancellation.Token);
        await stream.FirstDataWriteStarted.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
        await writer.DisposeAsync();
        byte[] bytes = stream.ToArray();
        Assert.IsFalse(bytes.AsSpan().EndsWith(Convert.FromHexString("000000000000000400000003")),
            "A cancelled, potentially partial frame must not be followed by STOP.");
    }

    private static global::Dnstap.Dnstap Sample() => new()
    {
        Type = global::Dnstap.Dnstap.Types.Type.Message,
        Identity = ByteString.CopyFromUtf8("tapper"),
        Message = new global::Dnstap.Message
        {
            Type = global::Dnstap.Message.Types.Type.ClientQuery,
            SocketFamily = global::Dnstap.SocketFamily.Inet,
            SocketProtocol = global::Dnstap.SocketProtocol.Udp,
            QueryAddress = ByteString.CopyFrom(new byte[] { 192, 0, 2, 10 }),
            ResponseAddress = ByteString.CopyFrom(new byte[] { 192, 0, 2, 53 }),
            QueryPort = 53000,
            ResponsePort = 53,
            QueryTimeSec = 1_700_000_000,
            QueryTimeNsec = 123_000_000,
            QueryMessage = ByteString.CopyFrom(Convert.FromHexString(
                "000101000001000000000000076578616D706C6503636F6D0000010001"))
        }
    };
}

internal sealed class YieldingWriteStream(bool blockFirstDataWrite = false) : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly TaskCompletionSource _firstData = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _overlapped;
    private int _writeCalls;

    public Task FirstDataWriteStarted => _firstData.Task;
    public bool Overlapped => Volatile.Read(ref _overlapped) != 0;
    public byte[] ToArray() => _buffer.ToArray();
    public void ReleaseBlockedWrite() => _resume.TrySetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        int active = Interlocked.Increment(ref _active);
        if (active > 1) Interlocked.Exchange(ref _overlapped, 1);
        try
        {
            int call = Interlocked.Increment(ref _writeCalls);
            await Task.Yield(); // Ensure the first call is still pending when subsequent writes begin.
            if (blockFirstDataWrite && call == 2)
            {
                _firstData.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
            }
            await _buffer.WriteAsync(data, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public override bool CanWrite => true;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => _buffer.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
