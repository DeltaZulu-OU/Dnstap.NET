using Dnstap.Protocol;
using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class DnstapWriterBehaviorTests
{
    [TestMethod]
    public void OfficialProtobufMessageRoundTripsWithoutChangingPacketBytes()
    {
        global::Dnstap.Dnstap expected = Sample();
        byte[] bytes = expected.ToByteArray();
        global::Dnstap.Dnstap actual = global::Dnstap.Dnstap.Parser.ParseFrom(bytes);

        Assert.IsTrue(actual.IsInitialized());
        Assert.AreEqual(expected.Type, actual.Type);
        Assert.AreEqual(expected.Identity, actual.Identity);
        Assert.AreEqual(expected.Message.Type, actual.Message.Type);
        Assert.AreEqual(expected.Message.SocketProtocol, actual.Message.SocketProtocol);
        Assert.AreEqual(expected.Message.QueryPort, actual.Message.QueryPort);
        Assert.AreEqual(expected.Message.QueryTimeSec, actual.Message.QueryTimeSec);
        Assert.AreEqual(expected.Message.QueryTimeNsec, actual.Message.QueryTimeNsec);
        Assert.AreEqual(expected.Message.QueryAddress, actual.Message.QueryAddress);
        Assert.AreEqual(expected.Message.QueryMessage, actual.Message.QueryMessage);
    }

    [TestMethod]
    public async Task FileWriterProducesValidFramesContainingProtobufMessages()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new DnstapWriter(stream))
        {
            await writer.StartAsync();
            await writer.WriteAsync(Sample());
            await writer.CompleteAsync();
        }

        stream.Position = 0;
        var records = new List<global::Dnstap.Dnstap>();
        await foreach (ReadOnlyMemory<byte> payload in FileFrameSession.ReadAsync(stream))
            records.Add(global::Dnstap.Dnstap.Parser.ParseFrom(payload.ToArray()));

        Assert.HasCount(1, records);
        Assert.IsTrue(records[0].IsInitialized());
        CollectionAssert.AreEqual(Sample().Message.QueryMessage.ToByteArray(), records[0].Message.QueryMessage.ToByteArray());
    }

    [TestMethod]
    public async Task MissingRequiredProtobufFieldsDoNotWriteAnyDataFrame()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new DnstapWriter(stream))
        {
            await writer.StartAsync();
            long before = stream.Length;
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => writer.WriteAsync(new global::Dnstap.Dnstap()));
            Assert.AreEqual(before, stream.Length);
            await writer.CompleteAsync();
        }
        stream.Position = 0;
        int count = 0;
        await foreach (ReadOnlyMemory<byte> ignored in FileFrameSession.ReadAsync(stream))
            count++;
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public void DuplexSenderModeMustBeSelectable()
    {
        Type? mode = typeof(DnstapWriter).Assembly.GetType("Dnstap.DnstapStreamMode");
        Assert.IsNotNull(mode, "The same public writer must support a duplex collector stream.");
        Assert.IsTrue(mode.IsEnum);
        Assert.IsTrue(Enum.GetNames(mode).Contains("Duplex"));
    }


    [TestMethod]
    public void FileWriterRejectsReadOnlyStreamAtConstruction()
    {
        using var stream = new MemoryStream(new byte[8], writable: false);
        Assert.ThrowsExactly<ArgumentException>(() => new DnstapWriter(stream));
    }

    [TestMethod]
    public async Task PreCancelledStartDoesNotPreventRetry()
    {
        await using var stream = new MemoryStream();
        await using var writer = new DnstapWriter(stream);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => writer.StartAsync(cancellation.Token));
        await writer.StartAsync();
        await writer.CompleteAsync();
        stream.Position = 0;
        int count = 0;
        await foreach (var _ in FileFrameSession.ReadAsync(stream))
            count++;
        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public async Task NestedMissingRequiredTypeIsRejectedBeforeDataWrite()
    {
        await using var stream = new MemoryStream();
        await using var writer = new DnstapWriter(stream);
        await writer.StartAsync();
        long before = stream.Length;
        var incomplete = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message()
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => writer.WriteAsync(incomplete));
        Assert.AreEqual(before, stream.Length);
        await writer.CompleteAsync();
    }

    [TestMethod]
    public async Task ExactMessageLimitIsAcceptedAndLargerMessageRejectedWithoutWriting()
    {
        var sample = Sample();
        await using var stream = new MemoryStream();
        await using var writer = new DnstapWriter(stream, maxDataLength: sample.CalculateSize());
        await writer.StartAsync();
        await writer.WriteAsync(sample);
        long before = stream.Length;
        sample.Identity = ByteString.CopyFromUtf8("a much longer test identity");
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => writer.WriteAsync(sample));
        Assert.AreEqual(before, stream.Length);
        await writer.CompleteAsync();
    }

    private static global::Dnstap.Dnstap Sample() => new()
    {
        Type = global::Dnstap.Dnstap.Types.Type.Message,
        Identity = ByteString.CopyFromUtf8("tapper"),
        Message = new global::Dnstap.Message
        {
            Type = global::Dnstap.Message.Types.Type.ClientQuery,
            SocketFamily = global::Dnstap.SocketFamily.Inet,
            SocketProtocol = global::Dnstap.SocketProtocol.Udp,
            QueryAddress = ByteString.CopyFrom(new byte[] { 192, 0, 2, 10 }),
            ResponseAddress = ByteString.CopyFrom(new byte[] { 192, 0, 2, 53 }),
            QueryPort = 53000,
            ResponsePort = 53,
            QueryTimeSec = 1_700_000_000,
            QueryTimeNsec = 123_000_000,
            QueryMessage = ByteString.CopyFrom(Convert.FromHexString(
                "000101000001000000000000076578616D706C6503636F6D0000010001"))
        }
    };
}
