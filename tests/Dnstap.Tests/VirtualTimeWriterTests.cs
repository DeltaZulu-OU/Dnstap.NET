using Dnstap.Protocol;
using Dnstap.Tests.Protocol;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class VirtualTimeWriterTests
{
    [TestMethod]
    public async Task DuplexHandshakeTimeoutUsesInjectedClockAndCancelsRead()
    {
        var clock = new FakeTimeProvider();
        await using var peer = new BlockingControlPeer([]);
        var writer = NewWriter(peer, clock);
        Task starting = writer.StartAsync();
        await peer.ReadStarted.WaitAsync(TimeSpan.FromSeconds(3));

        clock.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => starting.WaitAsync(TimeSpan.FromSeconds(3)));
        await peer.ReadCancellationObserved.WaitAsync(TimeSpan.FromSeconds(3));
        CollectionAssert.AreEqual(Ready, peer.Written);
        await writer.DisposeAsync();
    }

    [TestMethod]
    public async Task ConcurrentDuplexStartFailsImmediatelyEvenDuringPendingHandshake()
    {
        var clock = new FakeTimeProvider();
        await using var peer = new BlockingControlPeer([]);
        var writer = NewWriter(peer, clock);
        Task starting = writer.StartAsync();
        await peer.ReadStarted.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => writer.StartAsync().WaitAsync(TimeSpan.FromSeconds(3)));

        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => starting.WaitAsync(TimeSpan.FromSeconds(3)));
        await writer.DisposeAsync();
    }

    [TestMethod]
    public async Task DuplexFinishTimeoutUsesInjectedClockAndNeverRetriesRead()
    {
        var clock = new FakeTimeProvider();
        await using var peer = new BlockingControlPeer(WireFixture.Control(ControlTypes.Accept));
        var writer = NewWriter(peer, clock);
        await writer.StartAsync();
        Task completing = writer.CompleteAsync();
        await peer.ReadStarted.WaitAsync(TimeSpan.FromSeconds(3));

        clock.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => completing.WaitAsync(TimeSpan.FromSeconds(3)));
        int calls = peer.ReadCalls;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync());
        Assert.AreEqual(calls, peer.ReadCalls);
        CollectionAssert.AreEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        await writer.DisposeAsync();
    }

    [TestMethod]
    public async Task CallerCancellationDoesNotBecomeDeadlineTimeout()
    {
        var clock = new FakeTimeProvider();
        await using var peer = new BlockingControlPeer([]);
        var writer = NewWriter(peer, clock);
        using var cancellation = new CancellationTokenSource();
        Task starting = writer.StartAsync(cancellation.Token);
        await peer.ReadStarted.WaitAsync(TimeSpan.FromSeconds(3));

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => starting);
        await peer.ReadCancellationObserved.WaitAsync(TimeSpan.FromSeconds(3));
        // Advancing after cancellation must not resurrect a disposed deadline.
        clock.Advance(TimeSpan.FromSeconds(10));
        await writer.DisposeAsync();
    }

    private static DnstapWriter NewWriter(Stream stream, TimeProvider clock) =>
        new(stream, DnstapStreamMode.Duplex, leaveOpen: true,
            maxDataLength: 98_304, operationTimeout: TimeSpan.FromSeconds(10), timeProvider: clock);

    private static byte[] Ready => Convert.FromHexString(
        "0000000000000022000000040000000100000016" +
        "70726F746F6275663A646E737461702E446E73746170");

    private sealed class BlockingControlPeer(byte[] incoming) : Stream
    {
        private readonly MemoryStream _input = new(incoming);
        private readonly MemoryStream _output = new();
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCalls;
        private readonly TaskCompletionSource _readCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal byte[] Written => _output.ToArray();
        internal Task ReadStarted => _readStarted.Task;
        internal int ReadCalls => Volatile.Read(ref _readCalls);
        internal Task ReadCancellationObserved => _readCancelled.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCalls);
            if (_input.Position < _input.Length)
                return await _input.ReadAsync(destination, cancellationToken);
            _readStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _readCancelled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("Unreachable");
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default) => _output.WriteAsync(source, cancellationToken);

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
