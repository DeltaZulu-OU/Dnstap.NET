using System.Buffers.Binary;
using Dnstap.Protocol;
using Dnstap.Tests.Protocol;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class DuplexFailureMatrixTests
    {
        [TestMethod]
        [DataRow("00")]
        [DataRow("00000000")]
        [DataRow("000000000000")]
        [DataRow("00000000000000040000")]
        public async Task TruncatedAcceptFaultsNegotiationWithoutEmittingStart(string hex)
        {
            await using var peer = new ControlledPeer(Convert.FromHexString(hex));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(Ready, peer.Written);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
        }

        [TestMethod]
        [DataRow("000000000000000C000000010000006300000000")]
        [DataRow("000000000000000C000000010000000100000001")]
        [DataRow("00000000000000050000000100")]
        [DataRow("000000000000000400000005")]
        public async Task MalformedOrUnexpectedAcceptNeverEmitsStart(string hex)
        {
            await using var peer = new ControlledPeer(Convert.FromHexString(hex));
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.AreSequenceEqual(Ready, peer.Written);
        }

        [TestMethod]
        [DataRow("000000000000000C000000050000006300000000")]
        [DataRow("00000000000000050000000500")]
        [DataRow("000000000000000400000001")]
        public async Task InvalidFinishIsTerminalAndStopIsEmittedExactlyOnce(string finishHex)
        {
            var incoming = WireFixture.Concat(WireFixture.Control(ControlTypes.Accept),
                Convert.FromHexString(finishHex));
            await using var peer = new ControlledPeer(incoming);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            var reads = peer.ReadCalls;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreEqual(reads, peer.ReadCalls);
            Assert.AreSequenceEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        }

        [TestMethod]
        public async Task CallerCancellationDuringAcceptReadFaultsNegotiationWithoutStart()
        {
            await using var peer = new ControlledPeer([], suspendWhenInputExhausted: true);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            using var cancellation = new CancellationTokenSource();
            var starting = writer.StartAsync(cancellation.Token);
            await peer.ReadBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            cancellation.Cancel();

            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => starting);
            Assert.AreSequenceEqual(Ready, peer.Written);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
        }

        [TestMethod]
        public async Task CallerCancellationDuringFinishReadFaultsCompletionWithoutSecondStop()
        {
            await using var peer = new ControlledPeer(WireFixture.Control(ControlTypes.Accept),
                suspendWhenInputExhausted: true);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);
            await writer.StartAsync(TestContext.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            var completing = writer.CompleteAsync(cancellation.Token);
            await peer.ReadBlocked.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            cancellation.Cancel();

            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => completing);
            var reads = peer.ReadCalls;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            Assert.AreEqual(reads, peer.ReadCalls);
            Assert.AreSequenceEqual(WireFixture.Concat(Ready, WireFixture.Start, WireFixture.Stop), peer.Written);
        }

        [TestMethod]
        public async Task OversizedDeclaredAcceptLengthDoesNotReadBodyEvenIfBodyIsPresent()
        {
            var invalid = WireFixture.Control(ControlTypes.Accept, new byte[256], new byte[237]);
            Assert.AreEqual(513u, BinaryPrimitives.ReadUInt32BigEndian(invalid.AsSpan(4)));
            await using var peer = new ControlledPeer(invalid);
            await using var writer = new DnstapWriter(peer, DnstapStreamMode.Duplex);

            _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => writer.StartAsync(TestContext.CancellationToken));
            Assert.AreEqual(8, peer.BytesRead, "Only the 8-byte control header may be consumed.");
            Assert.AreSequenceEqual(Ready, peer.Written);
        }

        private static byte[] Ready => Convert.FromHexString(
            "0000000000000022000000040000000100000016" +
            "70726F746F6275663A646E737461702E446E73746170");

        private sealed class ControlledPeer(byte[] input, bool suspendWhenInputExhausted = false) : Stream
        {
            private readonly MemoryStream _input = new(input);
            private readonly MemoryStream _output = new();
            private readonly TaskCompletionSource _readBlocked =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal byte[] Written => _output.ToArray();
            internal Task ReadBlocked => _readBlocked.Task;
            internal int BytesRead { get; private set; }
            internal int ReadCalls { get; private set; }

            public override async ValueTask<int> ReadAsync(Memory<byte> destination,
                CancellationToken cancellationToken = default)
            {
                ReadCalls++;
                if (suspendWhenInputExhausted && _input.Position == _input.Length)
                {
                    _ = _readBlocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                var count = await _input.ReadAsync(destination[..Math.Min(destination.Length, 2)], cancellationToken);
                BytesRead += count;
                return count;
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

        public TestContext TestContext { get; set; }
    }
}
