using System.Text;
using Dnstap.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests.Protocol;

[TestClass]
public sealed class FrameWireSpecificationTests
{
    private const string ContentType = "protobuf:dnstap.Dnstap";

    [TestMethod]
    public void Fs001_EncodesDataUsingUnsignedBigEndianLength()
    {
        byte[] encoded = FrameCodec.EncodeData("abc"u8);
        CollectionAssert.AreEqual(Convert.FromHexString("00000003616263"), encoded);
    }

    [TestMethod]
    public void Fs001_EncodesStartWithBinaryContentTypeField()
    {
        var control = new ControlFrame(2, new[] { Encoding.ASCII.GetBytes(ContentType) });
        byte[] encoded = FrameCodec.EncodeControl(control);
        CollectionAssert.AreEqual(Convert.FromHexString(
            "0000000000000022000000020000000100000016" +
            "70726F746F6275663A646E737461702E446E73746170"), encoded);
    }

    [TestMethod]
    public void Fs001_DecodesDataIndependentlyOfOurEncoder()
    {
        Frame frame = FrameCodec.Decode(Convert.FromHexString("00000003616263"));
        Assert.IsFalse(frame.IsControl);
        CollectionAssert.AreEqual("abc"u8.ToArray(), frame.Data.ToArray());
    }

    [TestMethod]
    public void Fs001_DecodesKnownStopTypeFromWire()
    {
        Frame frame = FrameCodec.Decode(Convert.FromHexString("000000000000000400000003"));
        Assert.IsTrue(frame.IsControl);
        Assert.IsNotNull(frame.Control);
        Assert.AreEqual(3u, frame.Control.Type);
        Assert.AreEqual(0, frame.Control.ContentTypes.Count);
    }

    [TestMethod]
    [DataRow("0000000000000000")]
    [DataRow("0000000000000003")]
    [DataRow("0000000000000201")]
    public void Fs002_RejectsInvalidControlPayloadLength(string hex)
    {
        ExpectInvalid(() => FrameCodec.Decode(Convert.FromHexString(hex)));
    }

    [TestMethod]
    public void Fs003_RejectsOversizedContentTypeEvenInsideValidControlLength()
    {
        // control payload = 4(type) + 4(field) + 4(field length) + 257 bytes.
        byte[] bytes = Convert.FromHexString("000000000000010D000000020000000100000101")
            .Concat(new byte[257]).ToArray();
        ExpectInvalid(() => FrameCodec.Decode(bytes));
    }

    [TestMethod]
    public void Fs004_RejectsUnsignedMaximumBeforeReadingOrAllocating()
    {
        ExpectInvalid(() => FrameCodec.Decode(Convert.FromHexString("FFFFFFFF")));
    }

    [TestMethod]
    public void Fs005_RejectsEmptyAndOversizedOutgoingDataFrames()
    {
        ExpectInvalid(() => FrameCodec.EncodeData(ReadOnlySpan<byte>.Empty));
        ExpectInvalid(() => FrameCodec.EncodeData(new byte[(96 * 1024) + 1]));
    }

    [TestMethod]
    [DataRow("000000036162")]
    [DataRow("000000016162")]
    [DataRow("000000000000000500000003")]
    public void Fs006_RejectsTruncatedOrOverlongFrame(string hex)
    {
        ExpectInvalid(() => FrameCodec.Decode(Convert.FromHexString(hex)));
    }

    [TestMethod]
    public void Fs007_RejectsRepeatedStartContentTypes()
    {
        byte[] first = Encoding.ASCII.GetBytes(ContentType);
        byte[] control = Convert.FromHexString("000000000000003C000000020000000100000016")
            .Concat(first)
            .Concat(Convert.FromHexString("0000000100000016"))
            .Concat(first).ToArray();
        // 4 + 8 + 22 + 8 + 22 = 64 bytes, so update the declared length to 0x40.
        control[7] = 0x40;
        ExpectInvalid(() => FrameCodec.Decode(control));
    }

    [TestMethod]
    public void Fs007_RejectsContentTypeOnStop()
    {
        byte[] control = Convert.FromHexString("0000000000000022000000030000000100000016")
            .Concat(Encoding.ASCII.GetBytes(ContentType)).ToArray();
        ExpectInvalid(() => FrameCodec.Decode(control));
    }

    [TestMethod]
    public void Fs008_RejectsUnknownControlFieldType()
    {
        byte[] control = Convert.FromHexString("000000000000000C000000020000006300000000");
        ExpectInvalid(() => FrameCodec.Decode(control));
    }

    [TestMethod]
    public void Fs009_DecodesUnknownControlTypeForSessionLevelHandling()
    {
        Frame frame = FrameCodec.Decode(Convert.FromHexString("000000000000000400000080"));
        Assert.IsTrue(frame.IsControl);
        Assert.IsNotNull(frame.Control);
        Assert.AreEqual(128u, frame.Control.Type);
    }

    [TestMethod]
    public void Fs010_StartContentTypeIsOptionalAtWireLevel()
    {
        Frame frame = FrameCodec.Decode(Convert.FromHexString("000000000000000400000002"));
        Assert.IsNotNull(frame.Control);
        Assert.AreEqual(2u, frame.Control.Type);
        Assert.AreEqual(0, frame.Control.ContentTypes.Count);
    }

    [TestMethod]
    public void Fs010_ContentTypeIsComparedAsBinaryBytes()
    {
        var contentType = Encoding.ASCII.GetBytes(ContentType);
        contentType[0] = (byte)'P';
        Frame frame = FrameCodec.Decode(FrameFixture.StartWith(contentType));
        Assert.IsNotNull(frame.Control);
        CollectionAssert.AreEqual(contentType, frame.Control.ContentTypes[0]);
    }

    private static void ExpectInvalid(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        Assert.Fail("Expected InvalidDataException for malformed Frame Streams bytes.");
    }
}

internal static class FrameFixture
{
    internal static readonly byte[] Start = Convert.FromHexString(
        "0000000000000022000000020000000100000016" +
        "70726F746F6275663A646E737461702E446E73746170");
    internal static readonly byte[] Stop = Convert.FromHexString("000000000000000400000003");
    internal static readonly byte[] Data = Convert.FromHexString("00000003616263");

    internal static byte[] StartWith(byte[] contentType)
    {
        byte[] header = Convert.FromHexString("00000000000000000000000200000001");
        byte[] framed = new byte[20 + contentType.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(4), (uint)(12 + contentType.Length));
        header.AsSpan(8).CopyTo(framed.AsSpan(8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(16), (uint)contentType.Length);
        contentType.CopyTo(framed.AsSpan(20));
        return framed;
    }

    internal static byte[] Concat(params byte[][] inputs) => inputs.SelectMany(x => x).ToArray();
}
