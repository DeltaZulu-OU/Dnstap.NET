using System.Buffers;
using System.Buffers.Binary;
using Dnstap.Protocol;

namespace Dnstap.Tests.Protocol
{
    [TestClass]
    public sealed class FrameWireSpecificationTests
    {
        [TestMethod]
        public void Fs001_StartEncodingMatchesIndependentWireFixture()
        {
            var buffer = new ArrayBufferWriter<byte>();
            FrameCodec.WriteControl(buffer, new ControlFrame(2, new ReadOnlyMemory<byte>[] { WireFixture.ContentType }));
            Assert.AreSequenceEqual(WireFixture.Start, buffer.WrittenSpan.ToArray());
        }

        [TestMethod]
        public void Fs017_BorrowedFrameViewDoesNotCopyDecodedPayload()
        {
            var bytes = Convert.FromHexString("00000003616263");
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out var consumed));
            Assert.AreEqual(FrameKind.Data, frame.Kind);
            Assert.AreEqual(7L, consumed);
            bytes[4] = (byte)'z';
            Assert.AreSequenceEqual("zbc"u8.ToArray(), frame.Payload.ToArray());
        }

        [TestMethod]
        public void Fs001_StopDecodesWithMinimumFourByteControlLength()
        {
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(WireFixture.Stop, out var frame, out var consumed));
            Assert.AreEqual(FrameKind.Control, frame.Kind);
            Assert.AreEqual(12L, consumed);
            Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
            Assert.IsNotNull(control);
            Assert.AreEqual(3u, control.Type);
            Assert.IsEmpty(control.ContentTypes);
        }

        [TestMethod]
        public void Fs001_FramesCanBeCoalescedWithoutDiscardingFollowingBytes()
        {
            var frames = WireFixture.Concat(WireFixture.Data, WireFixture.Stop);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(frames, out var first, out var consumed));
            Assert.AreSequenceEqual("abc"u8.ToArray(), first.Payload.ToArray());
            Assert.AreEqual(7L, consumed);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(frames.AsSpan((int)consumed).ToArray(), out var second, out var remaining));
            Assert.AreEqual(FrameKind.Control, second.Kind);
            Assert.AreEqual(12L, remaining);
        }

        [TestMethod]
        public void Fs002_ControlPayloadExactly512BytesIsValid()
        {
            // 4-byte READY + (8 + 256) + (8 + 236) = 512.
            var bytes = WireFixture.Control(4, new byte[256], new byte[236]);
            Assert.HasCount(520, bytes);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out var consumed));
            Assert.AreEqual(520L, consumed);
            Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
            Assert.IsNotNull(control);
            Assert.HasCount(2, control.ContentTypes);
        }

        [TestMethod]
        [DataRow("0000000000000000")]
        [DataRow("0000000000000003")]
        [DataRow("0000000000000201")]
        public void Fs002_InvalidControlLengthIsRejectedImmediatelyWithoutPayload(string hex) => Assert.AreEqual(OperationStatus.InvalidData, WireFixture.Parse(Convert.FromHexString(hex), out _, out _));

        [TestMethod]
        public void Fs003_ContentTypeExactly256BytesIsValid()
        {
            var controlBytes = WireFixture.Control(2, new byte[256]);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(controlBytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
            Assert.IsNotNull(control);
            Assert.HasCount(256, control.ContentTypes.Single());
        }

        [TestMethod]
        public void Fs003_ContentType257BytesIsRejectedWithFullPayload()
        {
            var bytes = WireFixture.Control(2, new byte[257]);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.InvalidData, ControlCodec.TryDecode(frame.Payload, out _));
        }

        [TestMethod]
        public void Fs003_ZeroLengthContentTypeIsStructurallyValid()
        {
            var bytes = WireFixture.Control(2, Array.Empty<byte>());
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
            Assert.IsNotNull(control);
            Assert.HasCount(1, control.ContentTypes);
            Assert.HasCount(0, control.ContentTypes[0]);
        }

        [TestMethod]
        public void Fs003_ContentTypeCanContainZeroBytesWithoutTruncation()
        {
            byte[] binary = { (byte)'a', 0, (byte)'b' };
            var bytes = WireFixture.Control(2, binary);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
            Assert.IsNotNull(control);
            Assert.AreSequenceEqual(binary, control.ContentTypes.Single().ToArray());
        }

        [TestMethod]
        public void Fs004_DataAtConfiguredOneMiBReceiveLimitIsAccepted()
        {
            var bytes = new byte[4 + TestFrameCodec.DefaultMaxReceiveLength];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, TestFrameCodec.DefaultMaxReceiveLength);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out var consumed));
            Assert.AreEqual(bytes.Length, consumed);
            Assert.AreEqual(TestFrameCodec.DefaultMaxReceiveLength, frame.Payload.Length);
        }

        [TestMethod]
        public void Fs004_OneByteOverReceiveLimitIsRejectedBeforeReadingPayload()
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, TestFrameCodec.DefaultMaxReceiveLength + 1);
            Assert.AreEqual(OperationStatus.InvalidData, WireFixture.Parse(prefix, out _, out _));
        }


        [TestMethod]
        public void Fs004_CustomReceiveLimitControlsAcceptanceAndRejection()
        {
            var valid = new byte[4 + 16];
            BinaryPrimitives.WriteUInt32BigEndian(valid, 16);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(valid, out _, out _, maxDataLength: 16));
            Assert.AreEqual(OperationStatus.InvalidData, WireFixture.Parse(valid, out _, out _, maxDataLength: 15));
        }

        [TestMethod]
        public void Fs013_SegmentedSequenceSplitsHeaderAcrossTwoBuffers()
        {
            var first = new TestSegment(Convert.FromHexString("0000"));
            var second = first.Append(Convert.FromHexString("0003616263"));
            var sequence = new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
            Assert.AreEqual(OperationStatus.Done, TestFrameCodec.TryRead(sequence, out var frame, out var consumed));
            Assert.AreEqual(7L, consumed);
            Assert.AreSequenceEqual("abc"u8.ToArray(), frame.Payload.ToArray());
        }

        [TestMethod]
        public void Fs004_MaxUnsignedLengthIsRejectedWithoutAllocatingPayload() => Assert.AreEqual(OperationStatus.InvalidData, WireFixture.Parse(Convert.FromHexString("FFFFFFFF"), out _, out _));

        [TestMethod]
        [DataRow("00")]
        [DataRow("000000")]
        [DataRow("000000036162")]
        [DataRow("000000000000")]
        [DataRow("000000000000000500000003")]
        public void Fs006_IncompletePrefixOrPayloadNeedsMoreDataRatherThanInvalidData(string hex) => Assert.AreEqual(OperationStatus.NeedMoreData, WireFixture.Parse(Convert.FromHexString(hex), out _, out _));

        [TestMethod]
        public void Fs007_StartWithTwoContentTypeFieldsIsInvalid()
        {
            var bytes = WireFixture.Control(2, WireFixture.ContentType, WireFixture.ContentType);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.InvalidData, ControlCodec.TryDecode(frame.Payload, out _));
        }

        [TestMethod]
        public void Fs007_StopWithContentTypeIsInvalid()
        {
            var bytes = WireFixture.Control(3, WireFixture.ContentType);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.InvalidData, ControlCodec.TryDecode(frame.Payload, out _));
        }

        [TestMethod]
        public void Fs007_FinishWithContentTypeIsInvalid()
        {
            var bytes = WireFixture.Control(5, WireFixture.ContentType);
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.InvalidData, ControlCodec.TryDecode(frame.Payload, out _));
        }

        [TestMethod]
        public void Fs007_EncoderRejectsIllegalStopFields()
        {
            var output = new ArrayBufferWriter<byte>();
            _ = Assert.ThrowsExactly<ArgumentException>(() =>
                FrameCodec.WriteControl(output, new ControlFrame(3, new ReadOnlyMemory<byte>[] { WireFixture.ContentType })));
            Assert.AreEqual(0, output.WrittenCount);
        }

        [TestMethod]
        public void Fs003_EncoderRejectsContentTypeOver256BytesWithoutWriting()
        {
            var output = new ArrayBufferWriter<byte>();
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                FrameCodec.WriteControl(output, new ControlFrame(2, new ReadOnlyMemory<byte>[] { new byte[257] })));
            Assert.AreEqual(0, output.WrittenCount);
        }

        [TestMethod]
        public void Fs008_UnknownControlFieldTypeIsRejected()
        {
            var bytes = Convert.FromHexString("000000000000000C000000020000006300000000");
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.InvalidData, ControlCodec.TryDecode(frame.Payload, out _));
        }


        [TestMethod]
        [DataRow("0000000200")]
        [DataRow("000000020000000100000005")]
        [DataRow("0000000200000001FFFFFFFF")]
        public void IncompleteAndUnsignedOversizedControlFieldsAreRejected(string hex)
        {
            var payload = new ReadOnlySequence<byte>(Convert.FromHexString(hex));
            Assert.AreEqual(OperationStatus.InvalidData, ControlCodec.TryDecode(payload, out _));
        }

        [TestMethod]
        public void Fs009_UnknownControlTypeRemainsRepresentableForSessionPolicy()
        {
            var bytes = Convert.FromHexString("000000000000000400000080");
            Assert.AreEqual(OperationStatus.Done, WireFixture.Parse(bytes, out var frame, out _));
            Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
            Assert.IsNotNull(control);
            Assert.AreEqual(128u, control.Type);
        }

        [TestMethod]
        public void Fs010_StartWithoutContentTypeFieldDecodesSuccessfully()
        {
            Assert.AreEqual(OperationStatus.Done,
                WireFixture.Parse(Convert.FromHexString("000000000000000400000002"), out var frame, out _));
            Assert.AreEqual(OperationStatus.Done, ControlCodec.TryDecode(frame.Payload, out var control));
            Assert.IsNotNull(control);
            Assert.AreEqual(2u, control.Type);
            Assert.IsEmpty(control.ContentTypes);
        }
    }


    internal sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        internal TestSegment(ReadOnlyMemory<byte> memory) { Memory = memory; }
        internal TestSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new TestSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }

    internal static class WireFixture
    {
        internal static byte[] ContentType => "protobuf:dnstap.Dnstap"u8.ToArray();
        internal static byte[] Start => Convert.FromHexString(
            "0000000000000022000000020000000100000016" +
            "70726F746F6275663A646E737461702E446E73746170");
        internal static byte[] Stop => Convert.FromHexString("000000000000000400000003");
        internal static byte[] Data => Convert.FromHexString("00000003616263");

        internal static OperationStatus Parse(byte[] wire, out FrameSlice frame, out long consumed,
            int maxDataLength = TestFrameCodec.DefaultMaxReceiveLength) =>
            TestFrameCodec.TryRead(new ReadOnlySequence<byte>(wire), out frame, out consumed, maxDataLength);

        /// <summary>
        /// Test fixture construction is independent of the implementation's control encoder.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="types"></param>
        /// <returns></returns>
        internal static byte[] Control(uint type, params byte[][] types)
        {
            var size = 4 + types.Sum(t => 8 + t.Length);
            var wire = new byte[8 + size];
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(4), checked((uint)size));
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(8), type);
            var cursor = 12;
            foreach (var field in types)
            {
                BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(cursor), 1);
                BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(cursor + 4), checked((uint)field.Length));
                cursor += 8;
                field.AsSpan().CopyTo(wire.AsSpan(cursor));
                cursor += field.Length;
            }
            return wire;
        }

        internal static byte[] Concat(params byte[][] frames) => frames.SelectMany(f => f).ToArray();
    }
}
