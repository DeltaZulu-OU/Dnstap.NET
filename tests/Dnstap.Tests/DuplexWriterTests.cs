using System.Buffers;
using Dnstap.Protocol;
using Dnstap.Tests.Protocol;
using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class DuplexWriterTests
    {
        [TestMethod]
        public async Task DuplexWriterNegotiatesAndClosesAcrossFragmentedControlReads()
        {
            // Independent literal wire bytes: ACCEPT, then FINISH.
            var responses = Convert.FromHexString("000000000000000400000001000000000000000400000005");
            await using var duplex = new ScriptedDuplexStream(responses);
            await using (var writer = new DnstapWriter(duplex, DnstapStreamMode.Duplex))
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.WriteAsync(Message(), TestContext.CancellationToken);
                await writer.CompleteAsync(TestContext.CancellationToken);
                await writer.CompleteAsync(TestContext.CancellationToken);
            }

            var wire = duplex.Written;
            Assert.AreSequenceEqual(Convert.FromHexString(
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
            var position = 0;
            var types = new List<uint>();
            var records = 0;
            while (position < wire.Length)
            {
                var sequence = new ReadOnlySequence<byte>(wire.AsMemory(position));
                Assert.AreEqual(OperationStatus.Done,
                    TestFrameCodec.TryRead(sequence, out var frame, out var consumed));
                position += checked((int)consumed);
                if (frame.Kind == FrameKind.Control)
                {
                    Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
                    types.Add(control!.Type);
                }
                else
                {
                    var record = Dnstap.Parser.ParseFrom(frame.Payload.ToArray());
                    Assert.IsTrue(record.IsInitialized());
                    Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientQuery, record.Message.Type);
                    records++;
                }
            }

            Assert.AreSequenceEqual(new uint[] { ControlTypes.Ready, ControlTypes.Start, ControlTypes.Stop }, types);
            Assert.AreEqual(1, records);
        }

        [TestMethod]
        public async Task DuplexWriterRejectsUnexpectedAcceptWithoutSendingStart()
        {
            // The peer sends STOP instead of ACCEPT.
            await using var duplex = new ScriptedDuplexStream(Convert.FromHexString("000000000000000400000003"));
            await using (var writer = new DnstapWriter(duplex, DnstapStreamMode.Duplex))
            {
                _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            }

            var sequence = new ReadOnlySequence<byte>(duplex.Written);
            Assert.AreEqual(OperationStatus.Done, TestFrameCodec.TryRead(sequence, out var frame, out var consumed));
            Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
            Assert.AreEqual(ControlTypes.Ready, control!.Type);
            Assert.AreEqual(duplex.Written.Length, consumed, "START/STOP must not follow a failed negotiation.");
        }

        [TestMethod]
        public async Task DisposedDuplexWriterMustNotEmitReady()
        {
            await using var duplex = new ScriptedDuplexStream([]);
            var writer = new DnstapWriter(duplex, DnstapStreamMode.Duplex);
            await writer.DisposeAsync();

            _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.IsEmpty(duplex.Written);
        }

        [TestMethod]
        public async Task CompletedAndDisposedDuplexWriterRejectsFurtherCompletion()
        {
            var responses = Convert.FromHexString("000000000000000400000001000000000000000400000005");
            await using var duplex = new ScriptedDuplexStream(responses);
            var writer = new DnstapWriter(duplex, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            await writer.CompleteAsync(TestContext.CancellationToken);
            await writer.DisposeAsync();

            _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => writer.CompleteAsync(TestContext.CancellationToken));
        }


        [TestMethod]
        public async Task InterruptedFinishFaultsDuplexWriterAndCannotBeReadAgain()
        {
            // ACCEPT then incomplete FINISH frame header (peer closes after 6 of 8 header bytes).
            var controls = Convert.FromHexString("000000000000000400000001000000000000");
            await using var stream = new ScriptedDuplexStream(controls);
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            var previousReads = stream.ReadCalls;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreEqual(previousReads, stream.ReadCalls, "A failed FINISH must never be re-read.");
        }

        [TestMethod]
        public async Task PeerClosingBeforeAcceptIsInvalidProtocolData()
        {
            await using var stream = new ScriptedDuplexStream([]);
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task PeerClosingBeforeFinishIsInvalidProtocolData()
        {
            await using var stream = new ScriptedDuplexStream(Convert.FromHexString("000000000000000400000001"));
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync(TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task WrongAcceptContentTypeMustBeRejected()
        {
            var wrongAccept = Convert.FromHexString("00000000000000110000000100000001000000056F74686572");
            await using var stream = new ScriptedDuplexStream(wrongAccept);
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task WrongFinishControlTypeMustBeRejected()
        {
            var responses = Convert.FromHexString("000000000000000400000001000000000000000400000003");
            await using var stream = new ScriptedDuplexStream(responses);
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync(TestContext.CancellationToken));
        }


        [TestMethod]
        public async Task DuplexDisposeWithoutExplicitCompleteWaitsForFinish()
        {
            var responses = Convert.FromHexString("000000000000000400000001000000000000000400000005");
            await using var stream = new ScriptedDuplexStream(responses);
            var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            var before = stream.ReadCalls;
            await writer.DisposeAsync();
            Assert.IsGreaterThan(before, stream.ReadCalls, "Dispose must read FINISH after STOP.");
            Assert.AreSequenceEqual(WireFixture.Stop, stream.Written.AsSpan(stream.Written.Length - WireFixture.Stop.Length).ToArray());
        }

        [TestMethod]
        public async Task DuplexHandshakeIsBoundedWhenPeerNeverResponds()
        {
            var clock = new FakeTimeProvider();
            await using var stream = new ScriptedDuplexStream([], stallOnRead: true);
            var writer = NewTimedWriter(stream, clock);
            var starting = writer.StartAsync(TestContext.CancellationToken);
            await stream.ReadBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(10));
            _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => starting);
            await writer.DisposeAsync();
        }

        [TestMethod]
        public async Task StalledDuplexPayloadWriteCannotBlockDisposalForever()
        {
            var clock = new FakeTimeProvider();
            await using var stream = new ScriptedDuplexStream(
                Convert.FromHexString("000000000000000400000001"), stallWriteOn: 3);
            var writer = NewTimedWriter(stream, clock);
            await writer.StartAsync(TestContext.CancellationToken);
            var writing = writer.WriteAsync(Message(), TestContext.CancellationToken);
            await stream.WriteBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(10));
            _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => writing);
            await writer.DisposeAsync();
            Assert.AreEqual(3, stream.WriteCalls);
        }


        [TestMethod]
        public async Task StalledStopWriteTimesOutWithoutDependingOnCallerCancellation()
        {
            var clock = new FakeTimeProvider();
            await using var stream = new ScriptedDuplexStream(
                Convert.FromHexString("000000000000000400000001"), stallWriteOn: 3);
            var writer = NewTimedWriter(stream, clock);
            await writer.StartAsync(TestContext.CancellationToken);
            var completing = writer.CompleteAsync(TestContext.CancellationToken);
            await stream.WriteBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(10));
            _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => completing);
            await writer.DisposeAsync();
        }

        [TestMethod]
        public async Task StalledStopCannotHangImplicitDuplexDisposal()
        {
            var clock = new FakeTimeProvider();
            await using var stream = new ScriptedDuplexStream(
                Convert.FromHexString("000000000000000400000001"), stallWriteOn: 3);
            var writer = NewTimedWriter(stream, clock);
            await writer.StartAsync(TestContext.CancellationToken);
            var disposing = writer.DisposeAsync().AsTask();
            await stream.WriteBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(10));
            _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => disposing);
        }


        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task AcceptWithDnstapContentTypeIsAcceptedAndStartAdvertisesIt(bool withAlternative)
        {
            var accept = withAlternative
                ? WireFixture.Control(ControlTypes.Accept, "other"u8.ToArray(), WireFixture.ContentType)
                : WireFixture.Control(ControlTypes.Accept, WireFixture.ContentType);
            var responses = WireFixture.Concat(accept, WireFixture.Control(ControlTypes.Finish));
            await using var stream = new ScriptedDuplexStream(responses);
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            await writer.CompleteAsync(TestContext.CancellationToken);
            Assert.AreSequenceEqual(WireFixture.Concat(
                Convert.FromHexString("000000000000002200000004000000010000001670726F746F6275663A646E737461702E446E73746170"),
                WireFixture.Start, WireFixture.Stop), stream.Written);
        }

        [TestMethod]
        public async Task AcceptWithOneEmptyContentTypeIsRejected()
        {
            await using var stream = new ScriptedDuplexStream(
                WireFixture.Control(ControlTypes.Accept, Array.Empty<byte>()));
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task ImplicitDuplexDisposeRejectsWrongFinishAfterSendingStop()
        {
            await using var stream = new ScriptedDuplexStream(WireFixture.Concat(
                WireFixture.Control(ControlTypes.Accept), WireFixture.Stop));
            var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.DisposeAsync().AsTask());
            Assert.AreSequenceEqual(WireFixture.Stop, stream.Written.AsSpan(stream.Written.Length - WireFixture.Stop.Length).ToArray());
        }

        [TestMethod]
        public async Task TimedOutDuplexWriteCancelsCooperativeUnderlyingOperation()
        {
            // Repetition exposes races between the stream's cancellation and the wait timeout.
            for (var iteration = 0; iteration < 50; iteration++)
            {
                await using var stream = new ScriptedDuplexStream(
                    WireFixture.Control(ControlTypes.Accept),
                    stallWriteOn: 3, observeWriteCancellation: true);
                var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex,
                    operationTimeout: TimeSpan.FromMilliseconds(25));
                await writer.StartAsync(TestContext.CancellationToken);
                _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => writer.WriteAsync(Message(), TestContext.CancellationToken));
                Assert.IsTrue(await stream.AwaitStalledWriteCancellation(TimeSpan.FromSeconds(1)),
                    $"I/O cancellation was not propagated in iteration {iteration}.");
                await writer.DisposeAsync();
                Assert.AreSequenceEqual(WireFixture.Concat(
                    Convert.FromHexString("000000000000002200000004000000010000001670726F746F6275663A646E737461702E446E73746170"),
                    WireFixture.Start), stream.Written, "A failed DATA write must not be followed by STOP.");
            }
        }


        [TestMethod]
        public async Task DisposeRacingCompletionReleasesOwnedStreamEvenWhenLockWaitTimesOut()
        {
            var clock = new FakeTimeProvider();
            await using var stream = new ControlledShutdownStream();
            var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex,
                leaveOpen: false, maxDataLength: 98_304,
                operationTimeout: TimeSpan.FromSeconds(10), timeProvider: clock);
            await writer.StartAsync(TestContext.CancellationToken);

            var completing = writer.CompleteAsync(TestContext.CancellationToken);
            await stream.StopWriteStarted.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            var disposing = writer.DisposeAsync().AsTask();
            // Advance halfway through disposal's lock deadline, then let STOP finish.
            // The FINISH deadline now ends after the disposal lock deadline.
            clock.Advance(TimeSpan.FromSeconds(5));
            stream.ReleaseStopWrite();
            await stream.FinishReadStarted.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(5));

            _ = await Assert.ThrowsExactlyAsync<TimeoutException>(
                () => disposing.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken));
            Assert.AreEqual(1, stream.DisposeCalls, "Owned transport must be released even if the lock cannot be acquired.");
            _ = Assert.ThrowsExactly<ObjectDisposedException>(() => writer.WriteAsync(Message(), TestContext.CancellationToken));

            clock.Advance(TimeSpan.FromSeconds(5));
            _ = await Assert.ThrowsExactlyAsync<TimeoutException>(
                () => completing.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(1, stream.DisposeCalls);
        }


        [TestMethod]
        public async Task FS025_BufferedDuplexControlFramesMustFlushReadyAndStart()
        {
            await using var stream = new FlushGatedDuplexStream();
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            Assert.AreEqual(2, stream.FlushCalls, "Both READY and START must flush during StartAsync.");
            Assert.AreSequenceEqual(WireFixture.Concat(
                Convert.FromHexString("000000000000002200000004000000010000001670726F746F6275663A646E737461702E446E73746170"),
                WireFixture.Start), stream.Visible, "START must be visible before StartAsync returns.");
            await writer.CompleteAsync(TestContext.CancellationToken);
            Assert.AreEqual(3, stream.FlushCalls, "STOP uses the existing completion flush.");
        }

        /// <summary>A duplex peer cannot reply to buffered READY or see START until flushed.</summary>
        private sealed class FlushGatedDuplexStream : Stream
        {
            private readonly MemoryStream _pending = new();
            private readonly MemoryStream _visible = new();
            private readonly MemoryStream _responses = new(WireFixture.Concat(
                WireFixture.Control(ControlTypes.Accept), WireFixture.Control(ControlTypes.Finish)));
            internal int FlushCalls { get; private set; }
            internal byte[] Visible => _visible.ToArray();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var required = _responses.Position < 12
                    ? 42  // READY: 8-byte frame header + 34-byte control payload.
                    : 42 + WireFixture.Start.Length + WireFixture.Stop.Length;
                if (_visible.Length < required)
                {
                    throw new InvalidOperationException("Peer cannot respond before the control frame is flushed.");
                }
                return _responses.ReadAsync(buffer, cancellationToken);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                _pending.WriteAsync(buffer, cancellationToken);

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pending.Position = 0;
                _pending.CopyTo(_visible);
                _pending.SetLength(0);
                _pending.Position = 0;
                FlushCalls++;
                return Task.CompletedTask;
            }

            public override void Flush() => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private static DnstapWriter NewTimedWriter(Stream stream, TimeProvider clock) =>
            new(stream, DnstapStreamMode.Duplex, leaveOpen: true,
                maxDataLength: 98_304, operationTimeout: TimeSpan.FromSeconds(10), timeProvider: clock);

        private static Dnstap Message() => new()
        {
            Type = Dnstap.Types.Type.Message,
            Message = new Message
            {
                Type = global::Dnstap.Message.Types.Type.ClientQuery,
                QueryMessage = ByteString.CopyFrom(Convert.FromHexString("000101000001000000000000076578616D706C6503636F6D0000010001"))
            }
        };


        /// <summary>
        /// A deterministic peer that blocks a STOP write, then never replies with FINISH.
        /// This makes CompleteAsync hold the outer lock across two separate deadlines.
        /// </summary>
        private sealed class ControlledShutdownStream : Stream
        {
            private readonly MemoryStream _responses = new(WireFixture.Control(ControlTypes.Accept));
            private readonly TaskCompletionSource _stopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<int> _stalledFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _finishReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposeCalls;

            internal Task StopWriteStarted => _stopStarted.Task;
            internal Task FinishReadStarted => _finishReadStarted.Task;
            internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
            internal void ReleaseStopWrite() => _releaseStop.TrySetResult();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_responses.Position != _responses.Length)
                {
                    return _responses.ReadAsync(buffer, cancellationToken);
                }

                _ = _finishReadStarted.TrySetResult();
                return new ValueTask<int>(_stalledFinish.Task);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.Span.SequenceEqual(WireFixture.Stop))
                {
                    _ = _stopStarted.TrySetResult();
                    await _releaseStop.Task.WaitAsync(cancellationToken);
                }
            }

            public override ValueTask DisposeAsync()
            {
                _ = Interlocked.Increment(ref _disposeCalls);
                return ValueTask.CompletedTask;
            }

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

        private sealed class ScriptedDuplexStream(byte[] responses, bool stallOnRead = false, int stallWriteOn = 0,
            bool observeWriteCancellation = false) : Stream
        {
            private readonly MemoryStream _reads = new(responses);
            private readonly MemoryStream _writes = new();
            internal byte[] Written => _writes.ToArray();
            internal int ReadCalls { get; private set; }
            internal int WriteCalls { get; private set; }
            private readonly TaskCompletionSource<int> _stalledRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _readBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _writeBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task ReadBlocked => _readBlocked.Task;
            internal Task WriteBlocked => _writeBlocked.Task;
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
                    _ = _writeCancelled.TrySetResult();
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
                if (stallOnRead)
                {
                    _ = _readBlocked.TrySetResult();
                    return new ValueTask<int>(_stalledRead.Task);
                }
                return _reads.ReadAsync(destination[..Math.Min(destination.Length, 2)], cancellationToken);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
            {
                WriteCalls++;
                if (WriteCalls == stallWriteOn)
                {
                    _ = _writeBlocked.TrySetResult();
                    return new ValueTask(observeWriteCancellation
                        ? StallAndObserveCancellation(cancellationToken) : _stalledWrite.Task);
                }
                return _writes.WriteAsync(source, cancellationToken);
            }

            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        public TestContext TestContext { get; set; }
    }
}
