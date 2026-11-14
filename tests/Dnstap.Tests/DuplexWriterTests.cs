using System.Buffers;
using Dnstap.Protocol;
using Dnstap.Tests.Protocol;
using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class DuplexWriterTests
{
    [TestMethod]
    public async Task DuplexWriterNegotiatesAndClosesAcrossFragmentedControlReads()
    {
        // Independent literal wire bytes: ACCEPT, then FINISH.
        byte[] responses = Convert.FromHexString("000000000000000400000001000000000000000400000005");
        await using var duplex = new ScriptedDuplexStream(responses);
        await using (var writer = new DnstapWriter(duplex, DnstapStreamMode.Duplex))
        {
            await writer.StartAsync();
            await writer.WriteAsync(Message());
            await writer.CompleteAsync();
            await writer.CompleteAsync();
        }

        byte[] wire = duplex.Written;
        CollectionAssert.AreEqual(Convert.FromHexString(
            // READY with the dnstap content type.
            "0000000000000022000000040000000100000016" +
            "70726F746F6275663A646E737461702E446E73746170" +
            // START with the same content type.
            "0000000000000022000000020000000100000016" +
            "70726F746F6275663A646E737461702E446E73746170" +
            // DATA: 37-byte official protobuf message, with one query payload.
            "0000002572210805521D000101000001000000000000076578616D706C6503636F6D00000100017801" +
            // STOP.
            "000000000000000400000003"), wire);
        int position = 0;
        var types = new List<uint>();
        int records = 0;
        while (position < wire.Length)
        {
            var sequence = new ReadOnlySequence<byte>(wire.AsMemory(position));
            Assert.AreEqual(OperationStatus.Done,
                FrameCodec.TryRead(sequence, out FrameSlice frame, out long consumed));
            position += checked((int)consumed);
            if (frame.Kind == FrameKind.Control)
            {
                Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out ControlFrame? control));
                types.Add(control!.Type);
            }
            else
            {
                global::Dnstap.Dnstap record = global::Dnstap.Dnstap.Parser.ParseFrom(frame.Payload.ToArray());
                Assert.IsTrue(record.IsInitialized());
                Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientQuery, record.Message.Type);
                records++;
            }
        }

        CollectionAssert.AreEqual(new uint[] { ControlTypes.Ready, ControlTypes.Start, ControlTypes.Stop }, types);
        Assert.AreEqual(1, records);
    }

    [TestMethod]
    public async Task DuplexWriterRejectsUnexpectedAcceptWithoutSendingStart()
    {
        // The peer sends STOP instead of ACCEPT.
        await using var duplex = new ScriptedDuplexStream(Convert.FromHexString("000000000000000400000003"));
        await using (var writer = new DnstapWriter(duplex, DnstapStreamMode.Duplex))
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync());

        var sequence = new ReadOnlySequence<byte>(duplex.Written);
        Assert.AreEqual(OperationStatus.Done, FrameCodec.TryRead(sequence, out FrameSlice frame, out long consumed));
        Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out ControlFrame? control));
        Assert.AreEqual(ControlTypes.Ready, control!.Type);
        Assert.AreEqual((long)duplex.Written.Length, consumed, "START/STOP must not follow a failed negotiation.");
    }

    [TestMethod]
    public async Task DisposedDuplexWriterMustNotEmitReady()
    {
        await using var duplex = new ScriptedDuplexStream([]);
        var writer = new DnstapWriter(duplex, DnstapStreamMode.Duplex);
        await writer.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => writer.StartAsync());
        Assert.AreEqual(0, duplex.Written.Length);
    }

    [TestMethod]
    public async Task CompletedAndDisposedDuplexWriterRejectsFurtherCompletion()
    {
        byte[] responses = Convert.FromHexString("000000000000000400000001000000000000000400000005");
        await using var duplex = new ScriptedDuplexStream(responses);
        var writer = new DnstapWriter(duplex, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await writer.CompleteAsync();
        await writer.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => writer.CompleteAsync());
    }


    [TestMethod]
    public async Task InterruptedFinishFaultsDuplexWriterAndCannotBeReadAgain()
    {
        // ACCEPT then incomplete FINISH frame header (peer closes after 6 of 8 header bytes).
        byte[] controls = Convert.FromHexString("000000000000000400000001000000000000");
        await using var stream = new ScriptedDuplexStream(controls);
        await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync());
        int previousReads = stream.ReadCalls;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
        Assert.AreEqual(previousReads, stream.ReadCalls, "A failed FINISH must never be re-read.");
    }

    [TestMethod]
    public async Task PeerClosingBeforeAcceptIsInvalidProtocolData()
    {
        await using var stream = new ScriptedDuplexStream([]);
        await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync());
    }

    [TestMethod]
    public async Task PeerClosingBeforeFinishIsInvalidProtocolData()
    {
        await using var stream = new ScriptedDuplexStream(Convert.FromHexString("000000000000000400000001"));
        await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync());
    }

    [TestMethod]
    public async Task WrongAcceptContentTypeMustBeRejected()
    {
        byte[] wrongAccept = Convert.FromHexString("00000000000000110000000100000001000000056F74686572");
        await using var stream = new ScriptedDuplexStream(wrongAccept);
        await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync());
    }

    [TestMethod]
    public async Task WrongFinishControlTypeMustBeRejected()
    {
        byte[] responses = Convert.FromHexString("000000000000000400000001000000000000000400000003");
        await using var stream = new ScriptedDuplexStream(responses);
        await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync());
    }


    [TestMethod]
    public async Task DuplexDisposeWithoutExplicitCompleteWaitsForFinish()
    {
        byte[] responses = Convert.FromHexString("000000000000000400000001000000000000000400000005");
        await using var stream = new ScriptedDuplexStream(responses);
        var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        int before = stream.ReadCalls;
        await writer.DisposeAsync();
        Assert.IsGreaterThan(before, stream.ReadCalls, "Dispose must read FINISH after STOP.");
    }

    [TestMethod]
    public async Task DuplexHandshakeIsBoundedWhenPeerNeverResponds()
    {
        await using var stream = new ScriptedDuplexStream([], stallOnRead: true);
        var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex,
            operationTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => writer.StartAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task StalledDuplexPayloadWriteCannotBlockDisposalForever()
    {
        await using var stream = new ScriptedDuplexStream(
            Convert.FromHexString("000000000000000400000001"),
            stallWriteOn: 4);
        var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex,
            operationTimeout: TimeSpan.FromMilliseconds(100));
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => writer.WriteAsync(Message()).WaitAsync(TimeSpan.FromSeconds(3)));
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(4, stream.WriteCalls);
    }


    [TestMethod]
    public async Task StalledStopWriteTimesOutWithoutDependingOnCallerCancellation()
    {
        await using var stream = new ScriptedDuplexStream(
            Convert.FromHexString("000000000000000400000001"), stallWriteOn: 3);
        var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex,
            operationTimeout: TimeSpan.FromMilliseconds(100));
        await writer.StartAsync();
        Task completing = writer.CompleteAsync();
        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => completing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(completing.IsCompleted, "The writer must enforce the timeout itself.");
        await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task StalledStopCannotHangImplicitDuplexDisposal()
    {
        await using var stream = new ScriptedDuplexStream(
            Convert.FromHexString("000000000000000400000001"), stallWriteOn: 3);
        var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex,
            operationTimeout: TimeSpan.FromMilliseconds(100));
        await writer.StartAsync();
        Task disposing = writer.DisposeAsync().AsTask();
        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => disposing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(disposing.IsCompleted, "Dispose must enforce its own STOP timeout.");
    }


    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcceptWithDnstapContentTypeIsAcceptedAndStartAdvertisesIt(bool withAlternative)
    {
        byte[] accept = withAlternative
            ? WireFixture.Control(ControlTypes.Accept, "other"u8.ToArray(), WireFixture.ContentType)
            : WireFixture.Control(ControlTypes.Accept, WireFixture.ContentType);
        byte[] responses = WireFixture.Concat(accept, WireFixture.Control(ControlTypes.Finish));
        await using var stream = new ScriptedDuplexStream(responses);
        await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await writer.CompleteAsync();
        CollectionAssert.AreEqual(WireFixture.Concat(
            Convert.FromHexString("000000000000002200000004000000010000001670726F746F6275663A646E737461702E446E73746170"),
            WireFixture.Start, WireFixture.Stop), stream.Written);
    }

    [TestMethod]
    public async Task AcceptWithOneEmptyContentTypeIsRejected()
    {
        await using var stream = new ScriptedDuplexStream(
            WireFixture.Control(ControlTypes.Accept, Array.Empty<byte>()));
        await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync());
    }

    [TestMethod]
    public async Task ImplicitDuplexDisposeRejectsWrongFinishAfterSendingStop()
    {
        await using var stream = new ScriptedDuplexStream(WireFixture.Concat(
            WireFixture.Control(ControlTypes.Accept), WireFixture.Stop));
        var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.DisposeAsync().AsTask());
        CollectionAssert.AreEqual(WireFixture.Stop,
            stream.Written.AsSpan(stream.Written.Length - WireFixture.Stop.Length).ToArray());
    }

    [TestMethod]
    public async Task TimedOutDuplexWriteCancelsCooperativeUnderlyingOperation()
    {
        await using var stream = new ScriptedDuplexStream(
            WireFixture.Control(ControlTypes.Accept),
            stallWriteOn: 3, observeWriteCancellation: true);
        var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex,
            operationTimeout: TimeSpan.FromMilliseconds(150));
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => writer.WriteAsync(Message()));
        Assert.IsTrue(await stream.AwaitStalledWriteCancellation(TimeSpan.FromSeconds(2)),
            "The cancellation token supplied to the actual stream write must be cancelled.");
        await writer.DisposeAsync();
    }

    private static global::Dnstap.Dnstap Message() => new()
    {
        Type = global::Dnstap.Dnstap.Types.Type.Message,
        Message = new global::Dnstap.Message
        {
            Type = global::Dnstap.Message.Types.Type.ClientQuery,
            QueryMessage = ByteString.CopyFrom(Convert.FromHexString("000101000001000000000000076578616D706C6503636F6D0000010001"))
        }
    };

    private sealed class ScriptedDuplexStream(byte[] responses, bool stallOnRead = false, int stallWriteOn = 0,
        bool observeWriteCancellation = false) : Stream
    {
        private readonly MemoryStream _reads = new(responses);
        private readonly MemoryStream _writes = new();
        internal byte[] Written => _writes.ToArray();
        internal int ReadCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        private readonly TaskCompletionSource<int> _stalledRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stalledWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<bool> AwaitStalledWriteCancellation(TimeSpan timeout)
        {
            try { await _writeCancelled.Task.WaitAsync(timeout); return true; }
            catch (TimeoutException) { return false; }
        }
        private async Task StallAndObserveCancellation(CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _writeCancelled.TrySetResult();
                throw;
            }
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (stallOnRead) return new ValueTask<int>(_stalledRead.Task);
            return _reads.ReadAsync(destination[..Math.Min(destination.Length, 2)], cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (WriteCalls == stallWriteOn)
                return new ValueTask(observeWriteCancellation
                    ? StallAndObserveCancellation(cancellationToken) : _stalledWrite.Task);
            return _writes.WriteAsync(source, cancellationToken);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
