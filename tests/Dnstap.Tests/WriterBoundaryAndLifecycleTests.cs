using System.Buffers;
using System.Buffers.Binary;
using Dnstap.Protocol;
using Dnstap.Tests.Protocol;
using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class WriterBoundaryAndLifecycleTests
{
    [TestMethod]
    public async Task DuplexAcceptAt512ByteControlPayloadIsAccepted()
    {
        // 4-byte control type plus fields of 8+256, 8+206, and 8+22 bytes.
        byte[] accept = WireFixture.Control(ControlTypes.Accept,
            new byte[256], new byte[206], WireFixture.ContentType);
        Assert.AreEqual(520, accept.Length);
        Assert.AreEqual(512u, BinaryPrimitives.ReadUInt32BigEndian(accept.AsSpan(4)));

        await using var peer = new MeteredDuplexStream(WireFixture.Concat(
            accept, WireFixture.Control(ControlTypes.Finish)));
        await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await writer.CompleteAsync();

        CollectionAssert.AreEqual(
            WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        Assert.AreEqual(accept.Length + 12, peer.BytesRead);
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(3u)]
    [DataRow(513u)]
    [DataRow(uint.MaxValue)]
    public async Task DuplexRejectsInvalidAcceptLengthBeforeReadingPayload(uint declaredLength)
    {
        await using var peer = new MeteredDuplexStream(ControlHeader(declaredLength));
        await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync());
        Assert.AreEqual(8, peer.BytesRead, "Invalid control length must be rejected after the header.");
        CollectionAssert.AreEqual(Ready, peer.Written, "START must not be emitted.");
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(3u)]
    [DataRow(513u)]
    [DataRow(uint.MaxValue)]
    public async Task DuplexRejectsInvalidFinishLengthAndFaultsCompletion(uint declaredLength)
    {
        await using var peer = new MeteredDuplexStream(WireFixture.Concat(
            WireFixture.Control(ControlTypes.Accept), ControlHeader(declaredLength)));
        await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync());
        Assert.AreEqual(20, peer.BytesRead, "The invalid FINISH body must not be read.");
        CollectionAssert.AreEqual(
            WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
    }

    [TestMethod]
    public void ProductControlEncoderAccepts512BytesAndRejects513WithoutWriting()
    {
        var valid = new ControlFrame(ControlTypes.Ready,
            new ReadOnlyMemory<byte>[] { new byte[256], new byte[206], WireFixture.ContentType });
        var output = new ArrayBufferWriter<byte>();
        FrameCodec.WriteControl(output, valid);
        Assert.AreEqual(520, output.WrittenCount);
        Assert.AreEqual(512u, BinaryPrimitives.ReadUInt32BigEndian(output.WrittenSpan[4..]));
        Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(
            new ReadOnlySequence<byte>(output.WrittenMemory[8..]), out ControlFrame? decoded));
        Assert.HasCount(3, decoded!.ContentTypes);

        var invalid = new ControlFrame(ControlTypes.Ready,
            new ReadOnlyMemory<byte>[] { new byte[256], new byte[207], WireFixture.ContentType });
        var rejected = new ArrayBufferWriter<byte>();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => FrameCodec.WriteControl(rejected, invalid));
        Assert.AreEqual(0, rejected.WrittenCount);
    }

    [TestMethod]
    public void ProductControlDecoderRejectsPayloadLargerThan512()
    {
        byte[] payload = new byte[513];
        BinaryPrimitives.WriteUInt32BigEndian(payload, ControlTypes.Accept);
        Assert.AreEqual(OperationStatus.InvalidData,
            ControlCodec.TryDecode(new ReadOnlySequence<byte>(payload), out _));
    }

    [TestMethod]
    public async Task DefaultPublicDataLimitIsExactly98304Bytes()
    {
        // The expected size comes from the protocol, not FrameCodec.DefaultMaxSendLength.
        int payloadLength = 98_000;
        var record = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message
            {
                Type = global::Dnstap.Message.Types.Type.ClientQuery
            }
        };
        record.Extra = ByteString.CopyFrom(new byte[payloadLength]);
        payloadLength += 98_304 - record.CalculateSize();
        record.Extra = ByteString.CopyFrom(new byte[payloadLength]);
        Assert.AreEqual(98_304, record.CalculateSize());

        await using var file = new MemoryStream();
        await using var writer = new DnstapWriter(file);
        await writer.StartAsync();
        long before = file.Length;
        await writer.WriteAsync(record);
        Assert.AreEqual(before + 4 + 98_304, file.Length);

        record.Extra = ByteString.CopyFrom(new byte[payloadLength + 1]);
        Assert.AreEqual(98_305, record.CalculateSize());
        long acceptedLength = file.Length;
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => writer.WriteAsync(record));
        Assert.AreEqual(acceptedLength, file.Length, "Oversize record must not write a header.");
        await writer.CompleteAsync();
    }

    [TestMethod]
    public async Task DisposeDuringPendingDuplexStartStillWaitsForFinish()
    {
        // A START write is in flight while disposal examines the session state.
        await using var peer = new PausedStartDuplexStream(WireFixture.Concat(
            WireFixture.Control(ControlTypes.Accept), WireFixture.Control(ControlTypes.Finish)));
        var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
        Task starting = writer.StartAsync();
        await peer.StartWriteEntered.WaitAsync(TimeSpan.FromSeconds(3));

        Task disposing = writer.DisposeAsync().AsTask();
        peer.ReleaseStartWrite();
        await Task.WhenAll(starting, disposing).WaitAsync(TimeSpan.FromSeconds(3));

        CollectionAssert.AreEqual(
            WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        Assert.AreEqual(24, peer.BytesRead, "Graceful duplex disposal must consume FINISH.");
    }


    [TestMethod]
    public async Task DuplexRejects257ByteContentTypeEvenWithin512ByteFrame()
    {
        byte[] accept = WireFixture.Control(ControlTypes.Accept, new byte[257], WireFixture.ContentType);
        Assert.IsLessThan(520, accept.Length, "Only the content type, not the frame length, is invalid.");
        await using var peer = new MeteredDuplexStream(accept);
        await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync());
        CollectionAssert.AreEqual(Ready, peer.Written);
    }

    [TestMethod]
    public async Task DuplexRejectsTruncatedAcceptFieldHeader()
    {
        // Five-byte ACCEPT payload: control ID (4) and one truncated field-ID byte.
        byte[] control = Convert.FromHexString("00000000000000050000000100");
        await using var peer = new MeteredDuplexStream(control);
        await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync());
        Assert.AreEqual(control.Length, peer.BytesRead);
        CollectionAssert.AreEqual(Ready, peer.Written);
    }

    [TestMethod]
    public async Task DuplexRejectsFinishContainingContentType()
    {
        await using var peer = new MeteredDuplexStream(WireFixture.Concat(
            WireFixture.Control(ControlTypes.Accept),
            WireFixture.Control(ControlTypes.Finish, Array.Empty<byte>())));
        await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync());
        CollectionAssert.AreEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
    }

    [TestMethod]
    public async Task CallerCancellationDuringDuplexWriteFaultsSessionWithoutStop()
    {
        await using var peer = new BlockedDataDuplexStream(WireFixture.Control(ControlTypes.Accept));
        await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
        await writer.StartAsync();

        using var cancellation = new CancellationTokenSource();
        var record = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message { Type = global::Dnstap.Message.Types.Type.ClientQuery }
        };
        Task write = writer.WriteAsync(record, cancellation.Token);
        await peer.DataWriteEntered.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
        await writer.DisposeAsync();

        CollectionAssert.AreEqual(WireFixture.Concat(Ready, WireFixture.Start), peer.Written);
    }

    private static byte[] Ready => Convert.FromHexString(
        "0000000000000022000000040000000100000016" +
        "70726F746F6275663A646E737461702E446E73746170");

    private static byte[] ControlHeader(uint length)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), length);
        return header;
    }

    private class MeteredDuplexStream(byte[] incoming) : Stream
    {
        private readonly MemoryStream _input = new(incoming);
        private readonly MemoryStream _output = new();
        internal byte[] Written => _output.ToArray();
        internal int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int n = await _input.ReadAsync(buffer, cancellationToken);
            BytesRead += n;
            return n;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class PausedStartDuplexStream(byte[] incoming) : MeteredDuplexStream(incoming)
    {
        private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task StartWriteEntered => _startEntered.Task;
        internal void ReleaseStartWrite() => _resume.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Span.SequenceEqual(WireFixture.Start))
            {
                _startEntered.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
    private sealed class BlockedDataDuplexStream(byte[] incoming) : MeteredDuplexStream(incoming)
    {
        private readonly TaskCompletionSource _dataWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task DataWriteEntered => _dataWriteEntered.Task;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 4)
            {
                _dataWriteEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }

}
