using Dnstap.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests.Protocol;

[TestClass]
public sealed class FileSessionSpecificationTests
{
    [TestMethod]
    public async Task Fs011_AcceptsStartDataStopFromCanonicalWireBytes()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Data, WireFixture.Stop));
        var messages = await ReadAllAsync(stream);
        Assert.AreEqual(1, messages.Count);
        CollectionAssert.AreEqual("abc"u8.ToArray(), messages[0]);
    }

    [TestMethod]
    public async Task Fs010_StartWithoutContentTypeFieldIsAccepted()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Control(2), WireFixture.Data, WireFixture.Stop));
        var messages = await ReadAllAsync(stream);
        Assert.AreEqual(1, messages.Count);
    }

    [TestMethod]
    public async Task Fs011_EmptyFileIsInvalid()
    {
        await using var stream = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs011_StopBeforeStartIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Stop);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs011_DataBeforeStartIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(WireFixture.Data, WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs011_EofBeforeStopIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(WireFixture.Start, WireFixture.Data));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs006_TruncatedFrameAtEndOfFileIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, Convert.FromHexString("0000000361")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }


    [TestMethod]
    public async Task Fs004_FileReaderHonorsCustomReceiveLimit()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Data, WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await foreach (ReadOnlyMemory<byte> item in
                FileFrameSession.ReadAsync(stream, maxDataLength: 2))
            {
                _ = item.Length;
            }
        });
    }

    [TestMethod]
    public async Task Fs011_DuplicateStartIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Start, WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs011_TrailingDataAfterStopIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Stop, WireFixture.Data));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs011_TrailingSecondStopIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Stop, WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    [DataRow(1u)] // ACCEPT
    [DataRow(4u)] // READY
    [DataRow(5u)] // FINISH
    public async Task Fs011_HandshakeControlsInsideActiveFileStreamAreInvalid(uint type)
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Control(type), WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs011_ReadyBeforeStartIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Control(4), WireFixture.Start, WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs009_UnknownControlTypeIsIgnoredOnlyAfterStart()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Control(128), WireFixture.Data, WireFixture.Stop));
        var messages = await ReadAllAsync(stream);
        Assert.AreEqual(1, messages.Count);
        CollectionAssert.AreEqual("abc"u8.ToArray(), messages[0]);
    }

    [TestMethod]
    public async Task Fs009_UnknownControlBeforeStartIsInvalid()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Control(128), WireFixture.Start, WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs010_WrongContentTypeIsRejected()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Control(2, "different"u8.ToArray()), WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs010_ZeroLengthContentTypeIsPresentAndDoesNotMatch()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Control(2, Array.Empty<byte>()), WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs010_ContentTypeWithEmbeddedZeroByteDoesNotMatch()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Control(2, new byte[] { (byte)'p', 0, (byte)'b' }), WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs010_CaseChangedContentTypeDoesNotMatch()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Control(2, "Protobuf:dnstap.Dnstap"u8.ToArray()), WireFixture.Stop));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadAllAsync(stream));
    }

    [TestMethod]
    public async Task Fs013_HandlesOneBytePerReadWithoutMisclassifyingTruncation()
    {
        await using var stream = new OneByteStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Data, WireFixture.Stop));
        var messages = await ReadAllAsync(stream);
        CollectionAssert.AreEqual("abc"u8.ToArray(), messages.Single());
    }

    [TestMethod]
    public async Task Fs013_HandlesAdjacentDataFramesWithoutDroppingBytes()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Data, WireFixture.Data, WireFixture.Stop));
        var messages = await ReadAllAsync(stream);
        Assert.AreEqual(2, messages.Count);
    }

    [TestMethod]
    public async Task Fs011_ZeroDataFramesBetweenStartAndStopAreAllowed()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(WireFixture.Start, WireFixture.Stop));
        var messages = await ReadAllAsync(stream);
        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public async Task Fs011_FileWriterProducesCanonicalStartDataStop()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new FileFrameWriter(stream))
        {
            await writer.StartAsync();
            await writer.WriteAsync("abc"u8.ToArray());
            await writer.CompleteAsync();
        }
        CollectionAssert.AreEqual(
            WireFixture.Concat(WireFixture.Start, WireFixture.Data, WireFixture.Stop),
            stream.ToArray());
    }

    [TestMethod]
    public async Task Fs014_RepeatedCompletionAndDisposalWritesStopExactlyOnce()
    {
        await using var stream = new MemoryStream();
        var writer = new FileFrameWriter(stream);
        await writer.StartAsync();
        await writer.WriteAsync("abc"u8.ToArray());
        await writer.CompleteAsync();
        await writer.CompleteAsync();
        await writer.DisposeAsync();
        await writer.DisposeAsync();
        CollectionAssert.AreEqual(
            WireFixture.Concat(WireFixture.Start, WireFixture.Data, WireFixture.Stop),
            stream.ToArray());
    }

    [TestMethod]
    public async Task Fs014_OwnedTransportIsDisposedOnlyOnce()
    {
        var stream = new CountingStream();
        var writer = new FileFrameWriter(stream, leaveOpen: false);
        await writer.StartAsync();
        await writer.DisposeAsync();
        await writer.DisposeAsync();
        Assert.AreEqual(1, stream.DisposeCount);
    }


    [TestMethod]
    public async Task Fs014_DisposingUnstartedWriterCreatesNoStream()
    {
        await using var stream = new MemoryStream();
        var writer = new FileFrameWriter(stream);
        await writer.DisposeAsync();
        Assert.AreEqual(0, stream.Length);
    }

    [TestMethod]
    public async Task Fs014_FailedStartDoesNotEmitStopOnDisposal()
    {
        var stream = new ThrowOnFirstWriteStream();
        var writer = new FileFrameWriter(stream);
        await Assert.ThrowsExactlyAsync<IOException>(() => writer.StartAsync());
        await writer.DisposeAsync();
        Assert.AreEqual(1, stream.WriteAttempts, "No STOP write is allowed after a failed START.");
    }

    [TestMethod]
    public async Task Fs014_FailedDataWriteDoesNotEmitStopOnDisposal()
    {
        var stream = new ThrowOnSecondWriteStream();
        var writer = new FileFrameWriter(stream);
        await writer.StartAsync();
        await Assert.ThrowsExactlyAsync<IOException>(() => writer.WriteAsync("abc"u8.ToArray()));
        await writer.DisposeAsync();
        Assert.AreEqual(2, stream.WriteAttempts);
    }

    [TestMethod]
    public async Task Fs014_CompletionFlushesTheUnderlyingStream()
    {
        var stream = new FlushCountingStream();
        await using (var writer = new FileFrameWriter(stream))
        {
            await writer.StartAsync();
            await writer.CompleteAsync();
        }
        Assert.AreEqual(1, stream.FlushCount);
    }

    [TestMethod]
    public async Task Fs011_FileWriterDoesNotWriteDataBeforeStart()
    {
        await using var stream = new MemoryStream();
        await using var writer = new FileFrameWriter(stream);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await writer.WriteAsync("abc"u8.ToArray()));
        Assert.AreEqual(0, stream.Length);
    }

    [TestMethod]
    public async Task Fs011_FileWriterDoesNotWriteDataAfterCompletion()
    {
        await using var stream = new MemoryStream();
        await using var writer = new FileFrameWriter(stream);
        await writer.StartAsync();
        await writer.CompleteAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await writer.WriteAsync("abc"u8.ToArray()));
    }

    [TestMethod]
    public async Task Fs016_CancellationStopsFileReader()
    {
        await using var stream = new MemoryStream(WireFixture.Concat(
            WireFixture.Start, WireFixture.Data, WireFixture.Stop));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => ReadAllAsync(stream, cts.Token));
    }

    private static async Task<List<byte[]>> ReadAllAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var result = new List<byte[]>();
        await foreach (ReadOnlyMemory<byte> item in FileFrameSession.ReadAsync(stream, cancellationToken))
            result.Add(item.ToArray());
        return result;
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
            if (WriteAttempts == 2) throw new IOException("Injected DATA write failure.");
            return base.WriteAsync(buffer, cancellationToken);
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
}
