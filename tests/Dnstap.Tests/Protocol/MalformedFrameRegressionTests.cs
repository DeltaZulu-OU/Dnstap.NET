using System.Buffers;
using System.Buffers.Binary;
using Dnstap.Protocol;

namespace Dnstap.Tests.Protocol
{
    [TestClass]
    public sealed class MalformedFrameRegressionTests
    {
        [TestMethod]
        public void SeededMutationsOfFrameHeadersNeverCauseUncheckedParserFailures()
        {
            var random = new Random(0xF57A);
            for (int i = 0; i < 512; i++)
            {
                var wire = new byte[random.Next(1, 1050)];
                random.NextBytes(wire);
                // Exercise both arbitrary bytes and mutations of a known START frame.
                if (i % 2 == 0)
                {
                    var start = WireFixture.Start;
                    Array.Copy(start, wire, Math.Min(wire.Length, start.Length));
                    wire[random.Next(Math.Min(wire.Length, start.Length))] ^= (byte)(1 << random.Next(8));
                }
                var status = TestFrameCodec.TryRead(new ReadOnlySequence<byte>(wire),
                    out var frame, out var consumed);
                Assert.IsTrue(status is OperationStatus.Done or OperationStatus.NeedMoreData or OperationStatus.InvalidData);
                if (status == OperationStatus.Done)
                {
                    Assert.IsGreaterThan(0L, consumed);
                    Assert.IsTrue(consumed <= wire.Length);
                    if (frame.Kind == FrameKind.Control)
                    {
                        var controlStatus = ControlCodec.TryDecode(frame.Payload, out var control);
                        Assert.IsTrue(controlStatus is OperationStatus.Done or OperationStatus.InvalidData);
                        if (controlStatus == OperationStatus.Done)
                            Assert.IsNotNull(control);
                    }
                }
            }
        }

        [TestMethod]
        [DataRow(0u)]
        [DataRow(3u)]
        [DataRow(513u)]
        [DataRow(uint.MaxValue)]
        public void InvalidControlLengthIsRejectedEvenWithLargeFollowingPayload(uint length)
        {
            byte[] wire = new byte[1040];
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(4), length);
            Assert.AreEqual(OperationStatus.InvalidData,
                TestFrameCodec.TryRead(new ReadOnlySequence<byte>(wire), out _, out _));
        }

        [TestMethod]
        public async Task ShortRandomFilesNeverEscapeWithUncheckedParserExceptions()
        {
            var random = new Random(0xD57A);
            for (int i = 0; i < 48; i++)
            {
                byte[] wire = new byte[i];
                random.NextBytes(wire);
                await using var stream = new MemoryStream(wire);
                try
                {
                    await foreach (var _ in FileFrameSession.ReadAsync(
                        stream, maxDataLength: 256, cancellationToken: TestContext.CancellationToken))
                    {
                        Assert.Fail("Random bytes cannot form a canonical complete file session.");
                    }
                    Assert.Fail("Random bytes must not be accepted as a complete file session.");
                }
                catch (InvalidDataException)
                {
                    // Expected: malformed or truncated input.
                }
            }
        }

        public TestContext TestContext { get; set; } = null!;
    }
}
