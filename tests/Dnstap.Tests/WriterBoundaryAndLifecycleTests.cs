using System.Buffers;
using System.Buffers.Binary;
using Dnstap.Protocol;
using Dnstap.Tests.Protocol;
using Google.Protobuf;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class WriterBoundaryAndLifecycleTests
    {
        [TestMethod]
        public async Task DuplexAcceptAt512ByteControlPayloadIsAccepted()
        {
            // 4-byte control type plus fields of 8+256, 8+206, and 8+22 bytes.
            var accept = WireFixture.Control(ControlTypes.Accept,
                new byte[256], new byte[206], WireFixture.ContentType);
            Assert.HasCount(520, accept);
            Assert.AreEqual(512u, BinaryPrimitives.ReadUInt32BigEndian(accept.AsSpan(4)));

            await using var peer = new MeteredDuplexStream(WireFixture.Concat(
                accept, WireFixture.Control(ControlTypes.Finish)));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            await writer.CompleteAsync(TestContext.CancellationToken);

            Assert.AreSequenceEqual(
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
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.AreEqual(8, peer.BytesRead, "Invalid control length must be rejected after the header.");
            Assert.AreSequenceEqual(Ready, peer.Written, "START must not be emitted.");
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
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreEqual(20, peer.BytesRead, "The invalid FINISH body must not be read.");
            Assert.AreSequenceEqual(
                WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
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
                new ReadOnlySequence<byte>(output.WrittenMemory[8..]), out var decoded));
            Assert.HasCount(3, decoded!.ContentTypes);

            var invalid = new ControlFrame(ControlTypes.Ready,
                new ReadOnlyMemory<byte>[] { new byte[256], new byte[207], WireFixture.ContentType });
            var rejected = new ArrayBufferWriter<byte>();
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => FrameCodec.WriteControl(rejected, invalid));
            Assert.AreEqual(0, rejected.WrittenCount);
        }

        [TestMethod]
        public void ProductControlDecoderRejectsPayloadLargerThan512()
        {
            var payload = new byte[513];
            BinaryPrimitives.WriteUInt32BigEndian(payload, ControlTypes.Accept);
            Assert.AreEqual(OperationStatus.InvalidData,
                ControlCodec.TryDecode(new ReadOnlySequence<byte>(payload), out _));
        }

        [TestMethod]
        public async Task DefaultPublicDataLimitIsExactly98304Bytes()
        {
            // The expected size comes from the protocol, not FrameCodec.DefaultMaxSendLength.
            var payloadLength = 98_000;
            var record = new Dnstap
            {
                Type = Dnstap.Types.Type.Message,
                Message = new Message
                {
                    Type = Message.Types.Type.ClientQuery
                }
            };
            record.Extra = ByteString.CopyFrom(new byte[payloadLength]);
            payloadLength += 98_304 - record.CalculateSize();
            record.Extra = ByteString.CopyFrom(new byte[payloadLength]);
            Assert.AreEqual(98_304, record.CalculateSize());

            await using var file = new MemoryStream();
            await using var writer = new DnstapWriter(file);
            await writer.StartAsync(TestContext.CancellationToken);
            var before = file.Length;
            await writer.WriteAsync(record, TestContext.CancellationToken);
            Assert.AreEqual(before + 4 + 98_304, file.Length);

            record.Extra = ByteString.CopyFrom(new byte[payloadLength + 1]);
            Assert.AreEqual(98_305, record.CalculateSize());
            var acceptedLength = file.Length;
            _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => writer.WriteAsync(record, TestContext.CancellationToken));
            Assert.AreEqual(acceptedLength, file.Length, "Oversize record must not write a header.");
            await writer.CompleteAsync(TestContext.CancellationToken);
        }

        [TestMethod]
        public async Task DisposeDuringPendingDuplexStartStillWaitsForFinish()
        {
            // A START write is in flight while disposal examines the session state.
            await using var peer = new PausedStartDuplexStream(WireFixture.Concat(
                WireFixture.Control(ControlTypes.Accept), WireFixture.Control(ControlTypes.Finish)));
            var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            var starting = writer.StartAsync(TestContext.CancellationToken);
            await peer.StartWriteEntered.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);

            var disposing = writer.DisposeAsync().AsTask();
            peer.ReleaseStartWrite();
            await Task.WhenAll(starting, disposing).WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);

            Assert.AreSequenceEqual(
                WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
            Assert.AreEqual(24, peer.BytesRead, "Graceful duplex disposal must consume FINISH.");
        }


        [TestMethod]
        public async Task DuplexRejects257ByteContentTypeEvenWithin512ByteFrame()
        {
            var accept = WireFixture.Control(ControlTypes.Accept, new byte[257], WireFixture.ContentType);
            Assert.IsLessThan(520, accept.Length, "Only the content type, not the frame length, is invalid.");
            await using var peer = new MeteredDuplexStream(accept);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(Ready, peer.Written);
        }

        [TestMethod]
        public async Task DuplexRejectsTruncatedAcceptFieldHeader()
        {
            // Five-byte ACCEPT payload: control ID (4) and one truncated field-ID byte.
            var control = Convert.FromHexString("00000000000000050000000100");
            await using var peer = new MeteredDuplexStream(control);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.AreEqual(control.Length, peer.BytesRead);
            Assert.AreSequenceEqual(Ready, peer.Written);
        }

        [TestMethod]
        public async Task DuplexRejectsFinishContainingContentType()
        {
            await using var peer = new MeteredDuplexStream(WireFixture.Concat(
                WireFixture.Control(ControlTypes.Accept),
                WireFixture.Control(ControlTypes.Finish, Array.Empty<byte>())));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task CallerCancellationDuringDuplexWriteFaultsSessionWithoutStop()
        {
            await using var peer = new BlockedDataDuplexStream(WireFixture.Control(ControlTypes.Accept));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);

            using var cancellation = new CancellationTokenSource();
            var record = new Dnstap
            {
                Type = Dnstap.Types.Type.Message,
                Message = new Message { Type = Message.Types.Type.ClientQuery }
            };
            var write = writer.WriteAsync(record, cancellation.Token);
            await peer.DataWriteEntered.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            cancellation.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => write);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            await writer.DisposeAsync();

            Assert.AreSequenceEqual(WireFixture.Concat(Ready, WireFixture.Start), peer.Written);
        }


        [TestMethod]
        [DataRow("000000000000000400000080")] // Unknown control type.
        [DataRow("000000000000000C000000010000006300000000")] // Unknown field type.
        [DataRow("000000000000000C000000010000000100000001")] // Field length exceeds payload.
        [DataRow("000000000000000C0000000100000001FFFFFFFF")] // Unsigned overflow candidate.
        [DataRow("00000000000000050000000100")] // Truncated field header.
        [DataRow("000000030000000400000001")] // Data where control is required.
        public async Task DuplexRejectsMalformedAcceptWithoutSendingStart(string wire)
        {
            await using var peer = new MeteredDuplexStream(Convert.FromHexString(wire));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(Ready, peer.Written);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
        }

        [TestMethod]
        [DataRow("000000000000000400000080")] // Unknown control type.
        [DataRow("000000000000000C000000050000006300000000")] // Illegal field type.
        [DataRow("000000000000000C000000050000000100000001")] // Truncated field data.
        [DataRow("000000000000000C0000000500000001FFFFFFFF")] // Unsigned field length.
        [DataRow("00000000000000050000000500")] // Incomplete field header.
        [DataRow("000000030000000400000005")] // Data instead of FINISH control.
        public async Task DuplexRejectsMalformedFinishAndCannotRetry(string wire)
        {
            var incoming = WireFixture.Concat(
                WireFixture.Control(ControlTypes.Accept), Convert.FromHexString(wire));
            await using var peer = new MeteredDuplexStream(incoming);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            var reads = peer.BytesRead;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreEqual(reads, peer.BytesRead, "Faulted shutdown must never consume more input.");
            Assert.AreSequenceEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        }

        [TestMethod]
        [DataRow("000000000000000C00000001")] // Declares 12 bytes; only type present.
        [DataRow("000000000000000C0000000100000001")] // Partial field type.
        public async Task TruncatedAcceptPayloadIsRejectedWithoutStart(string wire)
        {
            await using var peer = new MeteredDuplexStream(Convert.FromHexString(wire));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(Ready, peer.Written);
        }

        [TestMethod]
        [DataRow("000000000000000C00000005")]
        [DataRow("000000000000000C0000000500000001")]
        public async Task TruncatedFinishPayloadFaultsSession(string wire)
        {
            await using var peer = new MeteredDuplexStream(WireFixture.Concat(
                WireFixture.Control(ControlTypes.Accept), Convert.FromHexString(wire)));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        }

        [TestMethod]
        public async Task AcceptMayListTheSameDnstapTypeMultipleTimes()
        {
            await using var peer = new MeteredDuplexStream(WireFixture.Concat(
                WireFixture.Control(ControlTypes.Accept,
                    "other"u8.ToArray(), WireFixture.ContentType, WireFixture.ContentType),
                WireFixture.Control(ControlTypes.Finish)));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            await writer.CompleteAsync(TestContext.CancellationToken);
            Assert.AreSequenceEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        }

        [TestMethod]
        public async Task CancellingAcceptReadFaultsHandshakeWithoutEmittingStartOrStop()
        {
            await using var peer = new PausedControlReadStream(
                WireFixture.Control(ControlTypes.Accept), pauseOnFinish: false);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            using var cancellation = new CancellationTokenSource();
            var starting = writer.StartAsync(cancellation.Token);
            await peer.ControlReadPaused.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            cancellation.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => starting);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(Ready, peer.Written);
        }

        [TestMethod]
        public async Task CancellingFinishReadFaultsShutdownWithoutRepeatingStop()
        {
            await using var peer = new PausedControlReadStream(
                WireFixture.Control(ControlTypes.Accept), pauseOnFinish: true);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var completing = writer.CompleteAsync(cancellation.Token);
            await peer.ControlReadPaused.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            cancellation.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => completing);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        }

        private sealed class PausedControlReadStream(byte[] incoming, bool pauseOnFinish) : MeteredDuplexStream(incoming)
        {
            private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task ControlReadPaused => _paused.Task;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                // Block the ACCEPT header, or the FINISH header after ACCEPT was consumed.
                if (!pauseOnFinish || BytesRead == 12)
                {
                    _ = _paused.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return await base.ReadAsync(buffer, cancellationToken);
            }
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
                var n = await _input.ReadAsync(buffer, cancellationToken);
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
                    _ = _startEntered.TrySetResult();
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
                if (buffer.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(buffer.Span) != 0)
                {
                    _ = _dataWriteEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                await base.WriteAsync(buffer, cancellationToken);
            }
        }

        public TestContext TestContext { get; set; }
    }
}
