using System.Buffers.Binary;
using Google.Protobuf;

namespace Dnstap.Tests.Protocol
{
    [TestClass]
    public sealed class FileSessionSpecificationTests
    {
        [TestMethod]
        public void Fs005_FileReaderRejectsInvalidArgumentsBeforeEnumeration()
        {
            using var stream = new MemoryStream();
            _ = Assert.ThrowsExactly<ArgumentNullException>(() => FileFrameSession.ReadAsync(null!));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FileFrameSession.ReadAsync(stream, maxDataLength: 0));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FileFrameSession.ReadAsync(stream, maxDataLength: int.MaxValue));
        }

        [TestMethod]
        public async Task Fs011_AcceptsStartDataStopFromCanonicalWireBytes()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Data, WireFixture.Stop));
            var messages = await ReadAllAsync(stream, TestContext.CancellationToken);
            Assert.HasCount(1, messages);
            Assert.AreSequenceEqual("abc"u8.ToArray(), messages[0]);
        }

        [TestMethod]
        public async Task Fs010_StartWithoutContentTypeFieldIsAccepted()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Control(2), WireFixture.Data, WireFixture.Stop));
            var messages = await ReadAllAsync(stream, TestContext.CancellationToken);
            Assert.HasCount(1, messages);
        }

        [TestMethod]
        public async Task Fs011_EmptyFileIsInvalid()
        {
            await using var stream = new MemoryStream();
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs011_StopBeforeStartIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Stop);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs011_DataBeforeStartIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(WireFixture.Data, WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs011_EofBeforeStopIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(WireFixture.Start, WireFixture.Data));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs006_TruncatedFrameAtEndOfFileIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, Convert.FromHexString("0000000361")));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }


        [TestMethod]
        public async Task Fs004_FileReaderHonorsCustomReceiveLimit()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Data, WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => {
                await foreach (var item in
                    FileFrameSession.ReadAsync(stream, maxDataLength: 2, TestContext.CancellationToken))
                {
                    _ = item.Length;
                }
            });
        }


        [TestMethod]
        public async Task Fs004_FileReaderAcceptsDataExactlyAtCustomReceiveLimit()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Data, WireFixture.Stop));
            var payloads = new List<byte[]>();
            await foreach (var payload in FileFrameSession.ReadAsync(stream, maxDataLength: 3, TestContext.CancellationToken))
            {
                payloads.Add(payload.ToArray());
            }

            Assert.HasCount(1, payloads);
            Assert.AreSequenceEqual("abc"u8.ToArray(), payloads[0]);
        }

        [TestMethod]
        public async Task Fs011_DuplicateStartIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Start, WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs011_TrailingDataAfterStopIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Stop, WireFixture.Data));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }


        [TestMethod]
        public async Task Fs011_RejectsTrailingByteDeliveredAfterStopInSeparateRead()
        {
            var frames = WireFixture.Concat(WireFixture.Start, WireFixture.Stop, "X"u8.ToArray());
            await using var stream = new BoundaryAwareReadStream(frames,
                WireFixture.Start.Length + WireFixture.Stop.Length);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs011_TrailingSecondStopIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Stop, WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        [DataRow(1u)] // ACCEPT
        [DataRow(4u)] // READY
        [DataRow(5u)] // FINISH
        public async Task Fs011_HandshakeControlsInsideActiveFileStreamAreInvalid(uint type)
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Control(type), WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs011_ReadyBeforeStartIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Control(4), WireFixture.Start, WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs009_UnknownControlTypeIsIgnoredOnlyAfterStart()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Control(128), WireFixture.Data, WireFixture.Stop));
            var messages = await ReadAllAsync(stream, TestContext.CancellationToken);
            Assert.HasCount(1, messages);
            Assert.AreSequenceEqual("abc"u8.ToArray(), messages[0]);
        }

        [TestMethod]
        public async Task Fs009_UnknownControlBeforeStartIsInvalid()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Control(128), WireFixture.Start, WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs010_WrongContentTypeIsRejected()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Control(2, "different"u8.ToArray()), WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs010_ZeroLengthContentTypeIsPresentAndDoesNotMatch()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Control(2, Array.Empty<byte>()), WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs010_ContentTypeWithEmbeddedZeroByteDoesNotMatch()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Control(2, new byte[] { (byte)'p', 0, (byte)'b' }), WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs010_CaseChangedContentTypeDoesNotMatch()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Control(2, "Protobuf:dnstap.Dnstap"u8.ToArray()), WireFixture.Stop));
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs013_HandlesOneBytePerReadWithoutMisclassifyingTruncation()
        {
            await using var stream = new OneByteStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Data, WireFixture.Stop));
            var messages = await ReadAllAsync(stream, TestContext.CancellationToken);
            Assert.AreSequenceEqual("abc"u8.ToArray(), messages.Single());
        }

        [TestMethod]
        public async Task Fs013_HandlesAdjacentDataFramesWithoutDroppingBytes()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Data, WireFixture.Data, WireFixture.Stop));
            var messages = await ReadAllAsync(stream, TestContext.CancellationToken);
            Assert.HasCount(2, messages);
        }

        [TestMethod]
        public async Task Fs011_ZeroDataFramesBetweenStartAndStopAreAllowed()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(WireFixture.Start, WireFixture.Stop));
            var messages = await ReadAllAsync(stream, TestContext.CancellationToken);
            Assert.IsEmpty(messages);
        }


        [TestMethod]
        public async Task Fs018_LargeThenManySmallFramesDoNotRetainOversizedReadBuffer()
        {
            const int largeLength = TestFrameCodec.DefaultMaxReceiveLength;
            const int smallCount = 10_000;
            var largeFrame = new byte[4 + largeLength];
            BinaryPrimitives.WriteUInt32BigEndian(largeFrame, largeLength);
            largeFrame[4] = 0x5A;
            byte[] tinyFrame = { 0, 0, 0, 1, 0x58 };
            var wire = WireFixture.Concat(WireFixture.Start,
                largeFrame, Enumerable.Repeat(tinyFrame, smallCount).SelectMany(x => x).ToArray(),
                WireFixture.Stop);

            await using var stream = new BoundaryAwareReadStream(wire,
                WireFixture.Start.Length + largeFrame.Length, maxRequestAfterBoundary: 65_536);
            var count = 0;
            await foreach (var item in FileFrameSession.ReadAsync(stream, cancellationToken: TestContext.CancellationToken))
            {
                count++;
                if (count == 1)
                {
                    Assert.HasCount(largeLength, item);
                    Assert.AreEqual((byte)0x5A, item.Span[0]);
                }
                else
                {
                    Assert.HasCount(1, item);
                    Assert.AreEqual((byte)0x58, item.Span[0]);
                }
            }
            Assert.AreEqual(smallCount + 1, count);
        }

        /// <summary>
        /// The sender tests below exercise the shipped public API, not a second
        /// Frame Streams writer maintained solely by this test project.
        /// </summary>
        [TestMethod]
        public async Task Fs011_PublicFileWriterProducesCanonicalStartDataStop()
        {
            await using var stream = new MemoryStream();
            var record = Example();
            await using (var writer = new DnstapWriter(stream))
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.WriteAsync(record, TestContext.CancellationToken);
                await writer.CompleteAsync(TestContext.CancellationToken);
            }

            var payload = record.ToByteArray();
            var expectedData = new byte[payload.Length + 4];
            BinaryPrimitives.WriteUInt32BigEndian(expectedData, (uint)payload.Length);
            payload.CopyTo(expectedData.AsSpan(4));
            Assert.AreSequenceEqual(
                WireFixture.Concat(WireFixture.Start, expectedData, WireFixture.Stop), stream.ToArray());
        }

        [TestMethod]
        public async Task Fs014_PublicRepeatedCompletionAndDisposalWritesStopOnce()
        {
            await using var stream = new MemoryStream();
            var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            await writer.WriteAsync(Example(), TestContext.CancellationToken);
            await writer.CompleteAsync(TestContext.CancellationToken);
            await writer.CompleteAsync(TestContext.CancellationToken);
            await writer.DisposeAsync();
            await writer.DisposeAsync();

            stream.Position = 0;
            Assert.HasCount(1, await ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task Fs005_CustomPublicFileLimitAcceptsExactBoundary()
        {
            await using var stream = new MemoryStream();
            var valid = Example();
            var exact = valid.CalculateSize();
            await using var writer = new DnstapWriter(stream, maxDataLength: exact);
            await writer.StartAsync(TestContext.CancellationToken);
            await writer.WriteAsync(valid, TestContext.CancellationToken);
            var before = stream.Length;
            valid.Extra = ByteString.CopyFromUtf8("one more field");
            _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => writer.WriteAsync(valid, TestContext.CancellationToken));
            Assert.AreEqual(before, stream.Length);
            await writer.CompleteAsync(TestContext.CancellationToken);

            stream.Position = 0;
            Assert.HasCount(1, await ReadAllAsync(stream, TestContext.CancellationToken));
        }

        [TestMethod]
        public void Fs005_InvalidPublicFileLimitIsRejected()
        {
            using var stream = new MemoryStream();
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DnstapWriter(stream, maxDataLength: 0));
        }

        [TestMethod]
        public async Task Fs014_PublicOwnedTransportIsDisposedExactlyOnce()
        {
            var stream = new CountingStream();
            var writer = new DnstapWriter(stream, leaveOpen: false);
            await writer.StartAsync(TestContext.CancellationToken);
            await writer.DisposeAsync();
            await writer.DisposeAsync();
            Assert.AreEqual(1, stream.DisposeCount);
        }

        [TestMethod]
        public async Task Fs014_PublicCompletionBeforeStartFails()
        {
            await using var stream = new MemoryStream();
            var writer = new DnstapWriter(stream);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(0L, stream.Length);
        }

        [TestMethod]
        public async Task Fs014_PublicCompletionAfterFailedDataWriteFails()
        {
            var stream = new ThrowOnSecondWriteStream();
            var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.WriteAsync(Example(), TestContext.CancellationToken));
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(2, stream.WriteAttempts);
        }

        [TestMethod]
        public async Task Fs014_PublicDisposalBeforeStartWritesNothing()
        {
            await using var stream = new MemoryStream();
            var writer = new DnstapWriter(stream);
            await writer.DisposeAsync();
            Assert.AreEqual(0L, stream.Length);
        }

        [TestMethod]
        public async Task Fs014_PublicFailedStartDoesNotWriteStop()
        {
            var stream = new ThrowOnFirstWriteStream();
            var writer = new DnstapWriter(stream);
            _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.StartAsync(TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(1, stream.WriteAttempts);
        }

        [TestMethod]
        public async Task Fs014_PublicFailedDataWriteDoesNotWriteStop()
        {
            var stream = new ThrowOnSecondWriteStream();
            var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.WriteAsync(Example(), TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(2, stream.WriteAttempts);
        }

        [TestMethod]
        public async Task Fs014_PublicCompletionFlushesStream()
        {
            var stream = new FlushCountingStream();
            await using (var writer = new DnstapWriter(stream))
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.CompleteAsync(TestContext.CancellationToken);
            }
            Assert.AreEqual(1, stream.FlushCount);
        }

        [TestMethod]
        public async Task Fs011_PublicWriterRejectsWritesBeforeStartAndAfterCompletion()
        {
            await using var stream = new MemoryStream();
            await using var writer = new DnstapWriter(stream);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.WriteAsync(Example(), TestContext.CancellationToken));
            Assert.AreEqual(0L, stream.Length);
            await writer.StartAsync(TestContext.CancellationToken);
            await writer.CompleteAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.WriteAsync(Example(), TestContext.CancellationToken));
        }

        private static Dnstap Example() => new()
        {
            Type = Dnstap.Types.Type.Message,
            Message = new Message
            {
                Type = Message.Types.Type.ClientQuery,
                QueryMessage = ByteString.CopyFromUtf8("abc")
            }
        };

        [TestMethod]
        public async Task Fs016_CancellationStopsFileReader()
        {
            await using var stream = new MemoryStream(WireFixture.Concat(
                WireFixture.Start, WireFixture.Data, WireFixture.Stop));
            using var cts = new CancellationTokenSource();
#pragma warning disable S6966
            cts.Cancel();
#pragma warning restore S6966
            _ = await Assert.ThrowsAsync<OperationCanceledException>(
                () => ReadAllAsync(stream, cts.Token));
        }

        private static async Task<List<byte[]>> ReadAllAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var result = new List<byte[]>();
            await foreach (var item in FileFrameSession.ReadAsync(stream, cancellationToken: cancellationToken))
            {
                result.Add(item.ToArray());
            }

            return result;
        }


        /// <summary>
        /// Enforces a read boundary after a large frame. An oversized subsequent read
        /// request means the reader retained its previous megabyte-scale buffer.
        /// </summary>
        /// <param name="wire">wire</param>
        /// <param name="boundary">boundary</param>
        /// <param name="maxRequestAfterBoundary">max request after boundary</param>
        private sealed class BoundaryAwareReadStream(
            byte[] wire, int boundary, int maxRequestAfterBoundary = int.MaxValue) : MemoryStream(wire)
        {
            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Position >= boundary && buffer.Length > maxRequestAfterBoundary)
                {
                    throw new InvalidOperationException("Large frame buffer was retained for small frames.");
                }

                var count = Position < boundary
                    ? Math.Min(buffer.Length, boundary - checked((int)Position))
                    : buffer.Length;
                return base.ReadAsync(buffer[..count], cancellationToken);
            }
        }

        private sealed class OneByteStream(byte[] wire) : MemoryStream(wire)
        {
            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        }


        private sealed class ThrowOnFirstWriteStream : MemoryStream
        {
            internal int WriteAttempts { get; private set; }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                WriteAttempts++;
                throw new IOException("Injected failure before START.");
            }
        }

        private sealed class ThrowOnSecondWriteStream : MemoryStream
        {
            internal int WriteAttempts { get; private set; }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                WriteAttempts++;
                return WriteAttempts == 2 ? throw new IOException("Injected DATA write failure.") : base.WriteAsync(buffer, cancellationToken);
            }
        }

        private sealed class FlushCountingStream : MemoryStream
        {
            internal int FlushCount { get; private set; }
            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                FlushCount++;
                return base.FlushAsync(cancellationToken);
            }
        }


        private sealed class CountingStream : MemoryStream
        {
            internal int DisposeCount { get; private set; }
            public override async ValueTask DisposeAsync()
            {
                DisposeCount++;
                await base.DisposeAsync();
            }
        }

        public TestContext TestContext { get; set; }
    }
}
