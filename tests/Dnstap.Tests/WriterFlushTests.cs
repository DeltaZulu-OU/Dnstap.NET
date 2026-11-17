using Dnstap.Protocol;
using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class WriterFlushTests
    {
        [TestMethod]
        public async Task FS026_FileFlushRequiresStartedStateAndEmitsNoFrame()
        {
            await using var stream = new FileFlushStream();
            var writer = new DnstapWriter(stream);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.FlushAsync());
            await writer.StartAsync(TestContext.CancellationToken);
            var before = stream.ToArray();
            await writer.FlushAsync(TestContext.CancellationToken);
            Assert.AreSequenceEqual(before, stream.ToArray());
            Assert.AreEqual(1, stream.FlushCalls);
            await writer.CompleteAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.FlushAsync());
            await writer.DisposeAsync();
            _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => writer.FlushAsync());
        }

        [TestMethod]
        public async Task FS026_DuplexFlushUsesWriterLockAndEmitsNoFrame()
        {
            await using var stream = new ControlledDuplexStream();
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            Assert.AreEqual(2, stream.FlushCalls, "READY and START are flushed during handshake.");
            var before = stream.Written;
            await writer.FlushAsync(TestContext.CancellationToken);
            Assert.AreEqual(3, stream.FlushCalls);
            Assert.AreSequenceEqual(before, stream.Written);
            await writer.CompleteAsync(TestContext.CancellationToken);
            Assert.AreEqual(4, stream.FlushCalls);
        }

        [TestMethod]
        public async Task FS026_FailedFileFlushFaultsSessionWithoutStop()
        {
            await using var stream = new FileFlushStream { FailFlush = true };
            await using var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            var before = stream.ToArray();
            _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.FlushAsync());
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
            Assert.AreSequenceEqual(before, stream.ToArray());
        }

        [TestMethod]
        public async Task FS026_CancelledDuplexFlushFaultsSession()
        {
            await using var stream = new ControlledDuplexStream { BlockThirdFlush = true };
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var flushing = writer.FlushAsync(cancellation.Token);
            await stream.FlushBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            cancellation.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => flushing);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
        }

        [TestMethod]
        public async Task FS026_DuplexFlushTimeoutFaultsSession()
        {
            var clock = new FakeTimeProvider();
            await using var stream = new ControlledDuplexStream { BlockThirdFlush = true };
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex,
                leaveOpen: true, maxDataLength: 98_304,
                operationTimeout: TimeSpan.FromSeconds(10), timeProvider: clock);
            await writer.StartAsync(TestContext.CancellationToken);
            var flushing = writer.FlushAsync(TestContext.CancellationToken);
            await stream.FlushBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            clock.Advance(TimeSpan.FromSeconds(10));
            _ = await Assert.ThrowsExactlyAsync<TimeoutException>(() => flushing);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
        }

        [TestMethod]
        public async Task FS026_CancellationWaitingForLockDoesNotFaultWriter()
        {
            await using var stream = new ControlledDuplexStream { BlockDataWrite = true };
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            var writing = writer.WriteAsync(new global::Dnstap.Dnstap
            {
                Type = global::Dnstap.Dnstap.Types.Type.Message,
                Message = new Message { Type = Message.Types.Type.ClientQuery }
            }, TestContext.CancellationToken);
            await stream.DataWriteBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var waiting = writer.FlushAsync(cancellation.Token);
            cancellation.Cancel();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
            stream.ReleaseDataWrite();
            await writing;
            await writer.FlushAsync(TestContext.CancellationToken);
            await writer.CompleteAsync(TestContext.CancellationToken);
        }

        public TestContext TestContext { get; set; } = null!;

        private sealed class FileFlushStream : MemoryStream
        {
            internal int FlushCalls { get; private set; }
            internal bool FailFlush { get; init; }
            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                FlushCalls++;
                if (FailFlush) throw new IOException("Test flush failure.");
                return base.FlushAsync(cancellationToken);
            }
        }

        private sealed class ControlledDuplexStream : Stream
        {
            private readonly MemoryStream _input = new(Convert.FromHexString(
                "000000000000000400000001000000000000000400000005"));
            private readonly MemoryStream _output = new();
            private readonly TaskCompletionSource _flushBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _dataBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseData = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool BlockThirdFlush { get; init; }
            internal bool BlockDataWrite { get; init; }
            internal int FlushCalls { get; private set; }
            internal int WriteCalls { get; private set; }
            internal byte[] Written => _output.ToArray();
            internal Task FlushBlocked => _flushBlocked.Task;
            internal Task DataWriteBlocked => _dataBlocked.Task;
            internal void ReleaseDataWrite() => _releaseData.TrySetResult();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                _input.ReadAsync(buffer, cancellationToken);

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                WriteCalls++;
                if (BlockDataWrite && WriteCalls == 3)
                {
                    _ = _dataBlocked.TrySetResult();
                    await _releaseData.Task.WaitAsync(cancellationToken);
                }
                await _output.WriteAsync(buffer, cancellationToken);
            }

            public override async Task FlushAsync(CancellationToken cancellationToken)
            {
                FlushCalls++;
                if (BlockThirdFlush && FlushCalls == 3)
                {
                    _ = _flushBlocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }

            public override void Flush() => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
