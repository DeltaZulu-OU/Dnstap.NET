using System.Buffers.Binary;
using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class NonCooperativeIoTests
{
    [TestMethod]
    public async Task TimedOutFrameRetainsItsOwnedBytesUntilNonCooperativeWriteCompletes()
    {
        var clock = new FakeTimeProvider();
        await using var peer = new LateCompletingDuplexStream();
        var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex,
            leaveOpen: true, maxDataLength: 98_304,
            operationTimeout: TimeSpan.FromSeconds(10), timeProvider: clock);

        await writer.StartAsync();
        var message = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message
            {
                Type = global::Dnstap.Message.Types.Type.ClientQuery,
                QueryMessage = ByteString.CopyFrom(new byte[] { 0, 1, 2, 3, 4 })
            }
        };
        byte[] expected = message.ToByteArray();
        Task writing = writer.WriteAsync(message);
        await peer.DataWriteStarted.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => writing.WaitAsync(TimeSpan.FromSeconds(3)));
        await writer.DisposeAsync();
        Assert.AreEqual(3, peer.WriteCalls, "A faulted writer must not send STOP on disposal.");

        // The stream retained the original ReadOnlyMemory after the caller timed out.
        ReadOnlyMemory<byte> pending = peer.PendingFrame;
        Assert.AreEqual(expected.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(pending.Span));
        CollectionAssert.AreEqual(expected, pending.Span[4..].ToArray());
        peer.CompletePendingWrite();
        await peer.DataWriteCompleted.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.WriteAsync(message));
    }

    private sealed class LateCompletingDuplexStream : Stream
    {
        private readonly MemoryStream _accept = new(Convert.FromHexString("000000000000000400000001"));
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task DataWriteStarted => _entered.Task;
        internal Task DataWriteCompleted => _finished.Task;
        internal ReadOnlyMemory<byte> PendingFrame { get; private set; }
        internal int WriteCalls { get; private set; }
        internal void CompletePendingWrite() => _released.TrySetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _accept.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (WriteCalls != 3) return; // READY and START are the first two writes.
            PendingFrame = buffer;
            _entered.TrySetResult();
            await _released.Task; // Intentionally ignores cancellation.
            _finished.TrySetResult();
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
