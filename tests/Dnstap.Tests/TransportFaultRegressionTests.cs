using Dnstap.Tests.Protocol;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class TransportFaultRegressionTests
    {
        private static global::Dnstap.Dnstap Sample() => new DnstapRecordFactory().Create(new DnsLogEntry
        {
            EventType = Message.Types.Type.ClientQuery,
            QueryPacket = new byte[] { 0xff, 0, 0x80 }
        });

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public async Task FileWriteIOExceptionIsTerminalAtStartDataAndStop(int failCall)
        {
            await using var stream = new FaultingStream(failWriteCall: failCall, partialWriteOnFailure: true);
            await using var writer = new DnstapWriter(stream);
            if (failCall == 1)
            {
                _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.StartAsync(TestContext.CancellationToken));
            }
            else
            {
                await writer.StartAsync(TestContext.CancellationToken);
                if (failCall == 2)
                    _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.WriteAsync(Sample(), TestContext.CancellationToken));
                else
                {
                    await writer.WriteAsync(Sample(), TestContext.CancellationToken);
                    _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.CompleteAsync(TestContext.CancellationToken));
                }
            }
            int writes = stream.WriteCalls;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(writes, stream.WriteCalls, "Faulted writers must not append STOP or retry a partial frame.");
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        public async Task DuplexWriteIOExceptionIsTerminalAtReadyStartDataAndStop(int failCall)
        {
            await using var stream = new FaultingStream(duplex: true, failWriteCall: failCall, partialWriteOnFailure: true);
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            if (failCall <= 2)
                _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.StartAsync(TestContext.CancellationToken));
            else
            {
                await writer.StartAsync(TestContext.CancellationToken);
                if (failCall == 3)
                    _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.WriteAsync(Sample(), TestContext.CancellationToken));
                else
                {
                    await writer.WriteAsync(Sample(), TestContext.CancellationToken);
                    _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.CompleteAsync(TestContext.CancellationToken));
                }
            }
            int writes = stream.WriteCalls;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(writes, stream.WriteCalls);
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public async Task DuplexFlushIOExceptionIsTerminalAtReadyStartAndStop(int failCall)
        {
            await using var stream = new FaultingStream(duplex: true, failFlushCall: failCall);
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            if (failCall <= 2)
                _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.StartAsync(TestContext.CancellationToken));
            else
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.WriteAsync(Sample(), TestContext.CancellationToken);
                _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            }
            int writes = stream.WriteCalls;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(writes, stream.WriteCalls);
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(3)]
        public async Task DuplexReadIOExceptionIsTerminalAtAcceptAndFinish(int failReadCall)
        {
            await using var stream = new FaultingStream(duplex: true, failReadCall: failReadCall);
            await using var writer = new DnstapWriter(stream, DnstapStreamMode.Duplex);
            if (failReadCall == 1)
                _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.StartAsync(TestContext.CancellationToken));
            else
            {
                await writer.StartAsync(TestContext.CancellationToken);
                await writer.WriteAsync(Sample(), TestContext.CancellationToken);
                _ = await Assert.ThrowsExactlyAsync<IOException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            }
            int writes = stream.WriteCalls;
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.CompleteAsync(TestContext.CancellationToken));
            await writer.DisposeAsync();
            Assert.AreEqual(writes, stream.WriteCalls);
        }

        [TestMethod]
        public async Task CompletionWaitsForAnAlreadyStartedWriteAndEmitsStopOnlyAfterData()
        {
            await using var stream = new PausedDataStream();
            await using var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            var writing = writer.WriteAsync(Sample(), TestContext.CancellationToken);
            await stream.DataStarted.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
            var completing = writer.CompleteAsync(TestContext.CancellationToken);
            Assert.IsFalse(completing.IsCompleted);
            stream.Release();
            await Task.WhenAll(writing, completing);
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => writer.WriteAsync(Sample(), TestContext.CancellationToken));

            await using var source = new MemoryStream(stream.ToArray());
            int records = 0;
            await foreach (var payload in FileFrameSession.ReadAsync(source, cancellationToken: TestContext.CancellationToken))
            {
                Assert.AreSequenceEqual(new byte[] { 0xff, 0, 0x80 },
                    global::Dnstap.Dnstap.Parser.ParseFrom(payload.ToArray()).Message.QueryMessage.ToByteArray());
                records++;
            }
            Assert.AreEqual(1, records);
        }

        [TestMethod]
        public async Task ConcurrentCompleteIsIdempotentAndProducesExactlyOneStop()
        {
            await using var stream = new MemoryStream();
            await using var writer = new DnstapWriter(stream);
            await writer.StartAsync(TestContext.CancellationToken);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => writer.CompleteAsync(TestContext.CancellationToken)));
            var bytes = stream.ToArray();
            Assert.AreSequenceEqual(WireFixture.Concat(WireFixture.Start, WireFixture.Stop), bytes);
        }

        [TestMethod]
        public async Task ConcurrentDisposalDisposesOwnedStreamAtMostOnce()
        {
            await using var stream = new DisposalCountingStream();
            var writer = new DnstapWriter(stream, leaveOpen: false);
            await writer.StartAsync(TestContext.CancellationToken);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => writer.DisposeAsync().AsTask()));
            Assert.AreEqual(1, stream.DisposeCalls);
            _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => writer.StartAsync(TestContext.CancellationToken));
        }

        public TestContext TestContext { get; set; } = null!;

        private sealed class FaultingStream(bool duplex = false, int failWriteCall = 0,
            int failFlushCall = 0, int failReadCall = 0, bool partialWriteOnFailure = false) : Stream
        {
            private readonly MemoryStream _written = new();
            private readonly MemoryStream _input = new(Convert.FromHexString(
                "000000000000000400000001000000000000000400000005"));
            internal int WriteCalls { get; private set; }
            private int _flushCalls;
            private int _readCalls;

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                WriteCalls++;
                if (WriteCalls == failWriteCall)
                {
                    if (partialWriteOnFailure && buffer.Length > 0)
                        await _written.WriteAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
                    throw new IOException("Injected partial transport write.");
                }
                await _written.WriteAsync(buffer, cancellationToken);
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                if (++_flushCalls == failFlushCall)
                    throw new IOException("Injected transport flush.");
                return Task.CompletedTask;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (++_readCalls == failReadCall)
                    throw new IOException("Injected transport read.");
                return _input.ReadAsync(buffer, cancellationToken);
            }

            public override bool CanWrite => true;
            public override bool CanRead => duplex;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class PausedDataStream : MemoryStream
        {
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _calls;
            internal Task DataStarted => _entered.Task;
            internal void Release() => _resume.TrySetResult();
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref _calls) == 2)
                {
                    _entered.TrySetResult();
                    await _resume.Task.WaitAsync(cancellationToken);
                }
                await base.WriteAsync(buffer, cancellationToken);
            }
        }

        private sealed class DisposalCountingStream : MemoryStream
        {
            internal int DisposeCalls;
            public override ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref DisposeCalls);
                return base.DisposeAsync();
            }
        }
    }
}
