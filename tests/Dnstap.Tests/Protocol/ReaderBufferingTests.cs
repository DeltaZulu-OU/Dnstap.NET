using System.Buffers.Binary;

namespace Dnstap.Tests.Protocol
{
    [TestClass]
    public sealed class ReaderBufferingTests
    {
        [TestMethod]
        public async Task Fs018_ShrinksAfterLargeFrameWithPartialNextHeaderBuffered()
        {
            const int smallCount = 10_000;
            var wire = CreateLargeAndSmallFrames(smallCount);
            var boundary = WireFixture.Start.Length + 4 + TestFrameCodec.DefaultMaxReceiveLength + 2;

            // Delivers the first two header bytes of the next frame with the large
            // frame. Requests after this boundary must use a normal-sized buffer.
            await using var stream = new BoundarySplitReadStream(wire, boundary);
            var messages = 0;
            await foreach (var payload in FileFrameSession.ReadAsync(stream, cancellationToken: TestContext.CancellationToken))
            {
                messages++;
                if (messages == 1)
                {
                    Assert.HasCount(TestFrameCodec.DefaultMaxReceiveLength, payload);
                }
                else
                {
                    Assert.HasCount(1, payload);
                }
            }

            Assert.AreEqual(smallCount + 1, messages);
            Assert.IsGreaterThan(0, stream.AfterBoundaryReadCalls, "The forced partial header must be read.");
        }

        [TestMethod]
        public async Task Fs020_UsesDeclaredFrameLengthToAvoidIncrementalBufferGrowth()
        {
            var wire = CreateLargeAndSmallFrames(0);
            await using var stream = new ReadRequestRecordingStream(wire);
            var messages = 0;
            await foreach (var payload in FileFrameSession.ReadAsync(stream, cancellationToken: TestContext.CancellationToken))
            {
                Assert.HasCount(TestFrameCodec.DefaultMaxReceiveLength, payload);
                messages++;
            }

            Assert.AreEqual(1, messages);
            Assert.IsGreaterThanOrEqualTo(2, stream.RequestLengths.Count);
            // The first read fills the initial 8 KiB buffer, which includes START
            // and the large data-frame header. The next rental should target the
            // known complete frame, not grow in a series of doubling allocations.
            Assert.IsGreaterThan(500_000, stream.RequestLengths[1],
                "Reader should size the next buffer request from the validated frame length.");
        }

        private static byte[] CreateLargeAndSmallFrames(int smallCount)
        {
            using var stream = new MemoryStream();
            stream.Write(WireFixture.Start);
            var large = TestFrameCodec.DefaultMaxReceiveLength;
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)large);
            stream.Write(header);
            stream.Write(new byte[large]);
            for (var i = 0; i < smallCount; i++)
            {
                stream.Write(new byte[] { 0, 0, 0, 1, 0x58 });
            }

            stream.Write(WireFixture.Stop);
            return stream.ToArray();
        }

        private sealed class BoundarySplitReadStream(byte[] wire, int boundary) : MemoryStream(wire)
        {
            internal int AfterBoundaryReadCalls { get; private set; }

            public override ValueTask<int> ReadAsync(
                Memory<byte> destination, CancellationToken cancellationToken = default)
            {
                if (Position >= boundary)
                {
                    AfterBoundaryReadCalls++;
                    if (destination.Length > 8192)
                    {
                        throw new InvalidOperationException("The reader retained the oversized buffer after a partial frame.");
                    }
                }

                var allowed = Position < boundary
                    ? Math.Min(destination.Length, boundary - checked((int)Position))
                    : destination.Length;
                return base.ReadAsync(destination[..allowed], cancellationToken);
            }
        }

        private sealed class ReadRequestRecordingStream(byte[] wire) : MemoryStream(wire)
        {
            internal List<int> RequestLengths { get; } = [];

            public override ValueTask<int> ReadAsync(
                Memory<byte> destination, CancellationToken cancellationToken = default)
            {
                RequestLengths.Add(destination.Length);
                return base.ReadAsync(destination, cancellationToken);
            }
        }

        public TestContext TestContext { get; set; }
    }
}
