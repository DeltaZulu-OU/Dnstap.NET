using Dnstap.Protocol;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests.Protocol;

[TestClass]
public sealed class FileSessionSpecificationTests
{
    [TestMethod]
    public async Task Fs011_ReadsStartDataStopFromKnownWireFixture()
    {
        await using var stream = new MemoryStream(FrameFixture.Concat(
            FrameFixture.Start, FrameFixture.Data, FrameFixture.Stop));
        var payloads = await ReadAllAsync(stream);
        Assert.AreEqual(1, payloads.Count);
        CollectionAssert.AreEqual("abc"u8.ToArray(), payloads[0]);
    }

    [TestMethod]
    public async Task Fs010_AcceptsStartWithoutContentType()
    {
        await using var stream = new MemoryStream(FrameFixture.Concat(
            Convert.FromHexString("000000000000000400000002"),
            FrameFixture.Data, FrameFixture.Stop));
        var payloads = await ReadAllAsync(stream);
        Assert.AreEqual(1, payloads.Count);
    }

    [TestMethod]
    public async Task Fs010_RejectsStartWithWrongContentType()
    {
        await using var stream = new MemoryStream(FrameFixture.Concat(
            FrameFixture.StartWith("wrong"u8.ToArray()),
            FrameFixture.Data, FrameFixture.Stop));
        await ExpectInvalidAsync(async () => { await ReadAllAsync(stream); });
    }

    [TestMethod]
    public async Task Fs011_RejectsDataBeforeStart()
    {
        await using var stream = new MemoryStream(FrameFixture.Concat(
            FrameFixture.Data, FrameFixture.Stop));
        await ExpectInvalidAsync(async () => { await ReadAllAsync(stream); });
    }

    [TestMethod]
    public async Task Fs011_RejectsMissingStopAndPrematureEof()
    {
        await using var stream = new MemoryStream(FrameFixture.Concat(
            FrameFixture.Start, FrameFixture.Data));
        await ExpectInvalidAsync(async () => { await ReadAllAsync(stream); });
    }

    [TestMethod]
    public async Task Fs011_RejectsDuplicateStart()
    {
        await using var stream = new MemoryStream(FrameFixture.Concat(
            FrameFixture.Start, FrameFixture.Start, FrameFixture.Stop));
        await ExpectInvalidAsync(async () => { await ReadAllAsync(stream); });
    }

    [TestMethod]
    public async Task Fs011_RejectsTrailingBytesAfterStop()
    {
        await using var stream = new MemoryStream(FrameFixture.Concat(
            FrameFixture.Start, FrameFixture.Stop, FrameFixture.Data));
        await ExpectInvalidAsync(async () => { await ReadAllAsync(stream); });
    }

    [TestMethod]
    public async Task Fs009_IgnoresUnknownControlTypeAfterStart()
    {
        await using var stream = new MemoryStream(FrameFixture.Concat(
            FrameFixture.Start,
            Convert.FromHexString("000000000000000400000080"),
            FrameFixture.Data, FrameFixture.Stop));
        var payloads = await ReadAllAsync(stream);
        Assert.AreEqual(1, payloads.Count);
    }

    [TestMethod]
    public async Task Fs013_HandlesSingleByteFragmentedReads()
    {
        await using var stream = new OneByteMemoryStream(FrameFixture.Concat(
            FrameFixture.Start, FrameFixture.Data, FrameFixture.Stop));
        var payloads = await ReadAllAsync(stream);
        CollectionAssert.AreEqual("abc"u8.ToArray(), payloads.Single());
    }

    [TestMethod]
    public async Task Fs005_Fs011_WritesTheCanonicalFileFraming()
    {
        await using var stream = new MemoryStream();
        await FileFrameSession.WriteAsync(stream, new ReadOnlyMemory<byte>[]
        {
            "abc"u8.ToArray()
        });
        CollectionAssert.AreEqual(FrameFixture.Concat(
            FrameFixture.Start, FrameFixture.Data, FrameFixture.Stop), stream.ToArray());
    }

    private static async Task<List<byte[]>> ReadAllAsync(Stream stream)
    {
        var payloads = new List<byte[]>();
        await foreach (ReadOnlyMemory<byte> data in FileFrameSession.ReadAsync(stream))
            payloads.Add(data.ToArray());
        return payloads;
    }

    private static async Task ExpectInvalidAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        Assert.Fail("Expected InvalidDataException for an invalid file session.");
    }

    private sealed class OneByteMemoryStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
