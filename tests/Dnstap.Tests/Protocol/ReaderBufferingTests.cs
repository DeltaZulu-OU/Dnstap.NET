using System.Buffers.Binary;
using Dnstap.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests.Protocol;

[TestClass]
public sealed class ReaderBufferingTests
{
    [TestMethod]
    public async Task Fs018_ShrinksAfterLargeFrameWithPartialNextHeaderBuffered()
    {
        const int smallCount = 10_000;
        byte[] wire = CreateLargeAndSmallFrames(smallCount);
        int boundary = WireFixture.Start.Length + 4 + TestFrameCodec.DefaultMaxReceiveLength + 2;

        // Delivers the first two header bytes of the next frame with the large
        // frame. Requests after this boundary must use a normal-sized buffer.
        await using var stream = new BoundarySplitReadStream(wire, boundary);
        int messages = 0;
        await foreach (ReadOnlyMemory<byte> payload in FileFrameSession.ReadAsync(stream))
        {
            messages++;
            if (messages == 1)
                Assert.AreEqual(TestFrameCodec.DefaultMaxReceiveLength, payload.Length);
            else
                Assert.AreEqual(1, payload.Length);
        }

        Assert.AreEqual(smallCount + 1, messages);
        Assert.IsTrue(stream.AfterBoundaryReadCalls > 0, "The forced partial header must be read.");
    }

    [TestMethod]
    public async Task Fs020_UsesDeclaredFrameLengthToAvoidIncrementalBufferGrowth()
    {
        byte[] wire = CreateLargeAndSmallFrames(0);
        await using var stream = new ReadRequestRecordingStream(wire);
        int messages = 0;
        await foreach (ReadOnlyMemory<byte> payload in FileFrameSession.ReadAsync(stream))
        {
            Assert.AreEqual(TestFrameCodec.DefaultMaxReceiveLength, payload.Length);
            messages++;
        }

        Assert.AreEqual(1, messages);
        Assert.IsTrue(stream.RequestLengths.Count >= 2);
        // The first read fills the initial 8 KiB buffer, which includes START
        // and the large data-frame header. The next rental should target the
        // known complete frame, not grow in a series of doubling allocations.
        Assert.IsTrue(stream.RequestLengths[1] > 500_000,
            "Reader should size the next buffer request from the validated frame length.");
    }

    private static byte[] CreateLargeAndSmallFrames(int smallCount)
    {
        using var stream = new MemoryStream();
        stream.Write(WireFixture.Start);
        int large = TestFrameCodec.DefaultMaxReceiveLength;
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)large);
        stream.Write(header);
        stream.Write(new byte[large]);
        for (int i = 0; i < smallCount; i++)
            stream.Write(new byte[] { 0, 0, 0, 1, 0x58 });
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
                    throw new InvalidOperationException("The reader retained the oversized buffer after a partial frame.");
            }

            int allowed = Position < boundary
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
}
