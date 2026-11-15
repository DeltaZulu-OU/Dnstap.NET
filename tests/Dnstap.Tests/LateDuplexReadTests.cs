using System.Buffers.Binary;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class LateDuplexReadTests
{
    [TestMethod]
    public async Task TimedOutFinishReadDoesNotRestartShutdownWhenUnderlyingReadCompletesLater()
    {
        var clock = new FakeTimeProvider();
        await using var peer = new LateFinishPeer();
        var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex,
            leaveOpen: true, maxDataLength: 98_304,
            operationTimeout: TimeSpan.FromSeconds(10), timeProvider: clock);

        await writer.StartAsync();
        Task completing = writer.CompleteAsync();
        await peer.FinishReadEntered.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => completing.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, peer.StopCount);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
        await writer.DisposeAsync();

        // A cancellation-ignoring stream may write into its borrowed buffer after timeout.
        // The writer must never issue a second STOP or read the FINISH again.
        peer.CompleteFinishRead();
        await peer.FinishReadCompleted.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, peer.StopCount);
        Assert.AreEqual(3, peer.ReadCalls);
    }

    private sealed class LateFinishPeer : Stream
    {
        private readonly MemoryStream _accept = new(Convert.FromHexString("000000000000000400000001"));
        private readonly TaskCompletionSource _readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task FinishReadEntered => _readEntered.Task;
        internal Task FinishReadCompleted => _readCompleted.Task;
        internal int ReadCalls { get; private set; }
        internal int StopCount { get; private set; }
        internal void CompleteFinishRead() => _readReleased.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (_accept.Position < _accept.Length)
                return await _accept.ReadAsync(destination, cancellationToken);
            _readEntered.TrySetResult();
            await _readReleased.Task; // Deliberately ignores cancellation.
            Convert.FromHexString("0000000000000004").AsSpan().CopyTo(destination.Span);
            _readCompleted.TrySetResult();
            return 8;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Span.SequenceEqual(Convert.FromHexString("000000000000000400000003")))
                StopCount++;
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
