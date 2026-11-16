using Dnstap.Tests.Protocol;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class WriterTransportOptionsTests
    {
        [TestMethod]
        public void DuplexRequiresReadableAndWritableStream()
        {
            using var writeOnly = new WriteOnlyStream();
            _ = Assert.ThrowsExactly<ArgumentException>(
                () => new DnstapWriter(writeOnly, DnstapStreamMode.Duplex));

            using var readOnly = new MemoryStream(new byte[8], writable: false);
            _ = Assert.ThrowsExactly<ArgumentException>(
                () => new DnstapWriter(readOnly, DnstapStreamMode.Duplex));
        }

        [TestMethod]
        public void RejectsUndefinedModeAndInvalidTimeoutAtConstruction()
        {
            using var stream = new MemoryStream();
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DnstapWriter(stream, (DnstapStreamMode)99));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DnstapWriter(stream, operationTimeout: TimeSpan.Zero));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DnstapWriter(stream, operationTimeout: TimeSpan.FromMilliseconds(-1)));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => new DnstapWriter(stream, operationTimeout: TimeSpan.FromDays(30)));
        }

        [TestMethod]
        public async Task DefaultOwnershipLeavesFileStreamOpenAfterCompletionAndDisposal()
        {
            await using var stream = new TrackedMemoryStream();
            await using (var writer = new DnstapWriter(stream))
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.CompleteAsync(TestContext.CancellationToken);
            }

            Assert.IsFalse(stream.Disposed);
            Assert.IsTrue(stream.CanWrite);
            Assert.AreSequenceEqual(
                WireFixture.Concat(WireFixture.Start, WireFixture.Stop), stream.ToArray());
        }

        [TestMethod]
        public async Task ExplicitOwnershipDisposesFileStreamAfterCompletion()
        {
            var stream = new TrackedMemoryStream();
            await using (var writer = new DnstapWriter(stream, leaveOpen: false))
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.CompleteAsync(TestContext.CancellationToken);
            }

            Assert.IsTrue(stream.Disposed);
        }

        [TestMethod]
        public async Task ExplicitFileModePreservesDefaultWireProtocol()
        {
            await using var implicitStream = new MemoryStream();
            await using var explicitStream = new MemoryStream();
            await using (var writer = new DnstapWriter(implicitStream))
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.CompleteAsync(TestContext.CancellationToken);
            }

            await using (var writer = new DnstapWriter(explicitStream, DnstapStreamMode.File))
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.CompleteAsync(TestContext.CancellationToken);
            }

            Assert.AreSequenceEqual(implicitStream.ToArray(), explicitStream.ToArray());
        }

        public TestContext TestContext { get; set; }

        private sealed class TrackedMemoryStream : MemoryStream
        {
            internal bool Disposed { get; private set; }

            public override ValueTask DisposeAsync()
            {
                Disposed = true;
                return base.DisposeAsync();
            }
        }

        private sealed class WriteOnlyStream : MemoryStream
        {
            public override bool CanRead => false;
        }
    }
}
