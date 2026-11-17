using Dnstap.Tests.Protocol;
using Google.Protobuf;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class RecordFactoryEdgeRegressionTests
    {
        [TestMethod]
        [DataRow("00")]
        [DataRow("0000000000000000000000")]
        [DataRow("000000000000000000000000")]
        [DataRow("C00C")]
        [DataRow("000101000002000000000000076578616D706C6503636F6D0000010001")]
        [DataRow("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
        public void MalformedDnsPacketsRemainOpaqueAndAreDefensivelyCopied(string hex)
        {
            byte[] query = Convert.FromHexString(hex);
            byte[] response = query.ToArray();
            byte[] expected = query.ToArray();
            var record = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientResponse,
                QueryPacket = query,
                ResponsePacket = response
            });
            query[0] ^= 0xff;
            response[0] ^= 0xff;
            var decoded = Dnstap.Parser.ParseFrom(record.ToByteArray());
            Assert.AreSequenceEqual(expected, decoded.Message.QueryMessage.ToByteArray());
            Assert.AreSequenceEqual(expected, decoded.Message.ResponseMessage.ToByteArray());
        }

        [TestMethod]
        public async Task DeterministicMalformedDnsPacketsSurviveWriterAndIndependentFrameReader()
        {
            var random = new Random(0x51A7);
            var packets = new List<byte[]>();
            await using var stream = new MemoryStream();
            await using (var writer = new DnstapWriter(stream))
            {
                await writer.StartAsync(TestContext.CancellationToken);
                for (int i = 0; i < 64; i++)
                {
                    byte[] packet = new byte[i + 1];
                    random.NextBytes(packet);
                    packets.Add(packet.ToArray());
                    await writer.WriteAsync(new DnstapRecordFactory().Create(new DnsLogEntry
                    {
                        EventType = Message.Types.Type.ClientQuery,
                        QueryPacket = packet
                    }), TestContext.CancellationToken);
                }
                await writer.CompleteAsync(TestContext.CancellationToken);
            }

            stream.Position = 0;
            int index = 0;
            await foreach (var payload in FileFrameSession.ReadAsync(stream, cancellationToken: TestContext.CancellationToken))
            {
                var record = Dnstap.Parser.ParseFrom(payload.ToArray());
                Assert.AreSequenceEqual(packets[index], record.Message.QueryMessage.ToByteArray());
                index++;
            }
            Assert.AreEqual(packets.Count, index);
        }

        [TestMethod]
        public void QueryZoneAcceptsExactLengthBoundariesAndBinaryLabels()
        {
            foreach (int size in new[] { 1, 3, 63, 64, 254, 255 })
            {
                byte[] zone = size switch
                {
                    1 => new byte[] { 0 },
                    3 => WireName(1),
                    63 => WireName(61),
                    64 => WireName(62),
                    254 => WireName(63, 63, 63, 60),
                    255 => WireName(63, 63, 63, 61),
                    _ => throw new InvalidOperationException()
                };
                Assert.AreEqual(size, zone.Length);
                byte[] expected = zone.ToArray();
                var record = new DnstapRecordFactory().Create(new DnsLogEntry
                {
                    EventType = Message.Types.Type.ClientQuery,
                    QueryZone = zone
                });
                zone[zone.Length == 1 ? 0 : 1] ^= 0xff;
                Assert.AreSequenceEqual(expected, record.Message.QueryZone.ToByteArray());
            }
        }

        [TestMethod]
        public void QueryZoneRejectsOverlongNamesAndLabelsWithoutTruncation()
        {
            byte[][] bad =
            [
                WireName(63, 63, 63, 62),
                [64, .. new byte[64], 0],
                [1, 0],
                [0, 0],
                [0xc0, 0x0c],
                [0x40, 0],
                [0x80, 0],
                [63, .. new byte[62], 0]
            ];
            foreach (byte[] value in bad)
            {
                _ = Assert.ThrowsExactly<ArgumentException>(() => new DnstapRecordFactory().Create(new DnsLogEntry
                {
                    EventType = Message.Types.Type.ClientQuery,
                    QueryZone = value
                }));
            }
        }

        [TestMethod]
        public void EmptyPacketsRemainAbsentAndZeroValuedOptionalPortsRemainPresent()
        {
            var record = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                QueryPacket = ReadOnlyMemory<byte>.Empty,
                ResponsePacket = ReadOnlyMemory<byte>.Empty,
                ClientPort = 0,
                ServerPort = ushort.MaxValue
            });
            var decoded = Dnstap.Parser.ParseFrom(record.ToByteArray());
            Assert.IsFalse(decoded.Message.HasQueryMessage);
            Assert.IsFalse(decoded.Message.HasResponseMessage);
            Assert.IsTrue(decoded.Message.HasQueryPort);
            Assert.AreEqual(0u, decoded.Message.QueryPort);
            Assert.AreEqual((uint)ushort.MaxValue, decoded.Message.ResponsePort);
        }

        [TestMethod]
        public void UnixEpochAndMaxDateTimeOffsetHaveValidNanosecondRanges()
        {
            foreach (var value in new[] { DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue })
            {
                var record = new DnstapRecordFactory().Create(new DnsLogEntry
                {
                    EventType = Message.Types.Type.ClientResponse,
                    QueryTimestamp = value,
                    ResponseTimestamp = value
                });
                Assert.IsTrue(record.Message.QueryTimeNsec < 1_000_000_000);
                Assert.IsTrue(record.Message.ResponseTimeNsec < 1_000_000_000);
                Assert.AreEqual(record.Message.QueryTimeNsec, record.Message.ResponseTimeNsec);
                Assert.AreEqual(record, Dnstap.Parser.ParseFrom(record.ToByteArray()));
            }
        }

        private static byte[] WireName(params int[] lengths)
        {
            using var output = new MemoryStream();
            foreach (int length in lengths)
            {
                output.WriteByte(checked((byte)length));
                for (int i = 0; i < length; i++)
                    output.WriteByte((byte)(i % 256));
            }
            output.WriteByte(0);
            return output.ToArray();
        }

        public TestContext TestContext { get; set; } = null!;
    }
}
