using Dnstap.Tests.Protocol;
using Google.Protobuf;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class WriterOwnershipAndRaceTests
    {
        private static Dnstap Sample() => new DnstapRecordFactory().Create(new DnsLogEntry
        {
            EventType = Message.Types.Type.ClientQuery,
            QueryPacket = new byte[] { 0x11, 0, 0xff }
        });

        [TestMethod]
        public async Task MutationAfterWriteAsyncReturnsDoesNotAlterPendingFrame()
        {
            await using var stream = new PausedOperationStream(pauseData: true);
            await using var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            var record = Sample();
            var writing = writer.WriteAsync(record, TestContext.CancellationToken);
            await stream.Blocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            try
            {
                // The public call synchronously validates and snapshots the protobuf into an owned frame.
                record.Message.QueryMessage = ByteString.CopyFrom(new byte[] { 0xaa });
                record.Message.Type = Message.Types.Type.ClientResponse;
            }
            finally
            {
                stream.Release();
            }
            await writing;
            await writer.CompleteAsync(TestContext.CancellationToken);
            await using var input = new MemoryStream(stream.ToArray());
            int records = 0;
            await foreach (var payload in FileFrameSession.ReadAsync(input, cancellationToken: TestContext.CancellationToken))
            {
                var decoded = Dnstap.Parser.ParseFrom(payload.ToArray());
                Assert.AreEqual(Message.Types.Type.ClientQuery, decoded.Message.Type);
                Assert.AreSequenceEqual(new byte[] { 0x11, 0, 0xff }, decoded.Message.QueryMessage.ToByteArray());
                records++;
            }
            Assert.AreEqual(1, records);
        }

        [TestMethod]
        public async Task FlushHoldingGateFinishesBeforeConcurrentCompletionWritesStop()
        {
            await using var stream = new PausedOperationStream(pauseFlush: true);
            await using var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            var flushing = writer.FlushAsync(TestContext.CancellationToken);
            await stream.Blocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            var completing = writer.CompleteAsync(TestContext.CancellationToken);
            Assert.IsFalse(completing.IsCompleted);
            try
            {
                Assert.AreSequenceEqual(WireFixture.Start, stream.ToArray(),
                    "STOP must not be written while an earlier flush holds the lock.");
            }
            finally
            {
                stream.Release();
            }
            await Task.WhenAll(flushing, completing);
            Assert.AreEqual(2, stream.FlushCalls, "Public flush and completion flush must each run exactly once.");
            Assert.AreSequenceEqual(WireFixture.Concat(WireFixture.Start, WireFixture.Stop), stream.ToArray());
        }

        [TestMethod]
        public async Task FlushQueuedBehindCompletionRejectsCompletedStateWithoutExtraFlush()
        {
            await using var stream = new PausedOperationStream(pauseStop: true);
            await using var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            var completing = writer.CompleteAsync(TestContext.CancellationToken);
            await stream.Blocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            var flushing = writer.FlushAsync(TestContext.CancellationToken);
            Assert.IsFalse(flushing.IsCompleted);
            stream.Release();
            await completing;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => flushing);
            Assert.AreEqual(1, stream.FlushCalls, "Only completion should reach the underlying flush.");
            Assert.AreSequenceEqual(WireFixture.Concat(WireFixture.Start, WireFixture.Stop), stream.ToArray());
        }

        public TestContext TestContext { get; set; } = null!;

        private sealed class PausedOperationStream(bool pauseData = false, bool pauseFlush = false, bool pauseStop = false)
            : MemoryStream
        {
            private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _writeCalls;
            internal Task Blocked => _blocked.Task;
            internal int FlushCalls { get; private set; }
            internal void Release() => _released.TrySetResult();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int call = Interlocked.Increment(ref _writeCalls);
                if ((pauseData || pauseStop) && call == 2)
                {
                    _blocked.TrySetResult();
                    await _released.Task.WaitAsync(cancellationToken);
                }
                await base.WriteAsync(buffer, cancellationToken);
            }

            public override async Task FlushAsync(CancellationToken cancellationToken)
            {
                FlushCalls++;
                if (pauseFlush && FlushCalls == 1)
                {
                    _blocked.TrySetResult();
                    await _released.Task.WaitAsync(cancellationToken);
                }
                await base.FlushAsync(cancellationToken);
            }
        }
    }
}
