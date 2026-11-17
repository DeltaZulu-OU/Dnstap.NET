using System.Net;
using Google.Protobuf;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class DnstapRecordFactoryTests
    {
        [TestMethod]
        public void CreatesMetadataOnlyClientResponseWithoutInventingDnsPackets()
        {
            var client = IPAddress.Parse("192.0.2.10");
            var server = IPAddress.Parse("192.0.2.53");
            var record = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientResponse,
                ClientAddress = client,
                ServerAddress = server,
                ClientPort = 53000,
                ServerPort = 53,
                Transport = SocketProtocol.Udp,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(9_999_999)
            });

            Assert.IsTrue(record.IsInitialized());
            Assert.AreEqual(Dnstap.Types.Type.Message, record.Type);
            Assert.AreEqual(Message.Types.Type.ClientResponse, record.Message.Type);
            Assert.AreEqual(SocketFamily.Inet, record.Message.SocketFamily);
            Assert.AreSequenceEqual(client.GetAddressBytes(), record.Message.QueryAddress.ToByteArray());
            Assert.AreSequenceEqual(server.GetAddressBytes(), record.Message.ResponseAddress.ToByteArray());
            Assert.AreEqual(53000u, record.Message.QueryPort);
            Assert.AreEqual(53u, record.Message.ResponsePort);
            Assert.AreEqual(SocketProtocol.Udp, record.Message.SocketProtocol);
            Assert.AreEqual(1_700_000_000ul, record.Message.ResponseTimeSec);
            Assert.AreEqual(999_999_900u, record.Message.ResponseTimeNsec);
            Assert.IsFalse(record.Message.HasQueryTimeSec);
            Assert.IsFalse(record.Message.HasQueryTimeNsec);
            Assert.IsFalse(record.Message.HasQueryMessage);
            Assert.IsFalse(record.Message.HasResponseMessage);
            Assert.AreEqual(record, Dnstap.Parser.ParseFrom(record.ToByteArray()));
        }

        [TestMethod]
        public void CreatesQueryWithIpv6AndCopiesBinaryPackets()
        {
            byte[] packet = [0, 255, 128, 1];
            var timestamp = new DateTimeOffset(2026, 11, 15, 15, 30, 0, TimeSpan.FromHours(2)).AddTicks(1234);
            var record = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ResolverQuery,
                ClientAddress = IPAddress.Parse("2001:db8::1"),
                ServerAddress = IPAddress.Parse("2001:db8::53"),
                Transport = SocketProtocol.Doq,
                Timestamp = timestamp,
                QueryPacket = packet
            });
            packet[1] = 0;

            Assert.AreEqual(Message.Types.Type.ResolverQuery, record.Message.Type);
            Assert.AreEqual(SocketFamily.Inet6, record.Message.SocketFamily);
            Assert.AreEqual(SocketProtocol.Doq, record.Message.SocketProtocol);
            Assert.AreEqual((ulong)timestamp.ToUnixTimeSeconds(), record.Message.QueryTimeSec);
            Assert.AreEqual(123_400u, record.Message.QueryTimeNsec);
            Assert.IsFalse(record.Message.HasResponseTimeSec);
            Assert.AreSequenceEqual(new byte[] { 0, 255, 128, 1 }, record.Message.QueryMessage.ToByteArray());
        }

        [TestMethod]
        public void AcceptsUnspecifiedOptionalMetadataWithoutSettingIt()
        {
            var record = new DnstapRecordFactory().Create(new DnsLogEntry { EventType = Message.Types.Type.StubQuery });
            Assert.IsTrue(record.IsInitialized());
            Assert.IsFalse(record.Message.HasSocketFamily);
            Assert.IsFalse(record.Message.HasSocketProtocol);
            Assert.IsFalse(record.Message.HasQueryAddress);
            Assert.IsFalse(record.Message.HasResponseAddress);
            Assert.IsFalse(record.Message.HasQueryPort);
            Assert.IsFalse(record.Message.HasResponsePort);
            Assert.IsFalse(record.Message.HasQueryTimeSec);
            Assert.IsFalse(record.Message.HasQueryMessage);
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(5)]
        [DataRow(6)]
        [DataRow(7)]
        [DataRow(8)]
        [DataRow(9)]
        [DataRow(10)]
        [DataRow(11)]
        [DataRow(12)]
        [DataRow(13)]
        [DataRow(14)]
        public void PreservesEveryOfficialEventTypeAndTimestampDirection(int numericValue)
        {
            var type = (Message.Types.Type)numericValue;
            var message = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = type,
                Timestamp = DateTimeOffset.UnixEpoch.AddTicks(1)
            }).Message;
            Assert.AreEqual(type, message.Type);
            var response = numericValue % 2 == 0;
            if (response)
            {
                Assert.IsTrue(message.HasResponseTimeSec);
                Assert.AreEqual(100u, message.ResponseTimeNsec);
                Assert.IsFalse(message.HasQueryTimeSec);
            }
            else
            {
                Assert.IsTrue(message.HasQueryTimeSec);
                Assert.AreEqual(100u, message.QueryTimeNsec);
                Assert.IsFalse(message.HasResponseTimeSec);
            }
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(5)]
        [DataRow(6)]
        [DataRow(7)]
        public void PreservesEveryOfficialSocketProtocol(int numericValue)
        {
            var protocol = (SocketProtocol)numericValue;
            var record = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                Transport = protocol
            });
            Assert.AreEqual(protocol, record.Message.SocketProtocol);
        }

        [TestMethod]
        public void RejectsUnknownEventTypeAndSocketProtocol()
        {
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DnstapRecordFactory().Create(
                new DnsLogEntry { EventType = 0 }));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DnstapRecordFactory().Create(
                new DnsLogEntry { EventType = (Message.Types.Type)99 }));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DnstapRecordFactory().Create(
                new DnsLogEntry { EventType = Message.Types.Type.ClientQuery, Transport = (SocketProtocol)99 }));
        }

        [TestMethod]
        public void RejectsMixedIpFamiliesAndScopedIpv6()
        {
            _ = Assert.ThrowsExactly<ArgumentException>(() => new DnstapRecordFactory().Create(
                new DnsLogEntry
                {
                    EventType = Message.Types.Type.ClientQuery,
                    ClientAddress = IPAddress.Parse("192.0.2.10"),
                    ServerAddress = IPAddress.Parse("2001:db8::1")
                }));

            _ = Assert.ThrowsExactly<ArgumentException>(() => new DnstapRecordFactory().Create(
                new DnsLogEntry
                {
                    EventType = Message.Types.Type.ClientQuery,
                    ClientAddress = new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(), 7)
                }));
        }

        [TestMethod]
        public void AcceptsIpv4MappedIpv6AsIpv6ButDoesNotNormalizeIt()
        {
            var mapped = IPAddress.Parse("::ffff:192.0.2.10");
            var result = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                ClientAddress = mapped,
                ServerAddress = IPAddress.IPv6Loopback
            });
            Assert.AreEqual(SocketFamily.Inet6, result.Message.SocketFamily);
            Assert.AreSequenceEqual(mapped.GetAddressBytes(), result.Message.QueryAddress.ToByteArray());
        }

        [TestMethod]
        public void RejectsPreEpochTimestampAndNullEntry()
        {
            _ = Assert.ThrowsExactly<ArgumentNullException>(() => new DnstapRecordFactory().Create(null!));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DnstapRecordFactory().Create(
                new DnsLogEntry
                {
                    EventType = Message.Types.Type.ClientQuery,
                    Timestamp = DateTimeOffset.UnixEpoch.AddTicks(-1)
                }));
        }

        [TestMethod]
        public void ExplicitZeroPortsRemainPresent()
        {
            var result = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                ClientPort = 0,
                ServerPort = ushort.MaxValue
            });
            Assert.IsTrue(result.Message.HasQueryPort);
            Assert.AreEqual(0u, result.Message.QueryPort);
            Assert.AreEqual(ushort.MaxValue, result.Message.ResponsePort);
        }

        [TestMethod]
        public void ProducerIdentityAndVersionAreCopiedOnceAndSharedImmutably()
        {
            byte[] identity = [0x74, 0x61, 0x70, 0xff];
            byte[] version = [0, 0x31, 0x00];
            var factory = new DnstapRecordFactory(identity, version);
            identity[0] = 0;
            version[1] = 0;
            var first = factory.Create(new DnsLogEntry { EventType = Message.Types.Type.ClientQuery });
            var second = factory.Create(new DnsLogEntry { EventType = Message.Types.Type.ClientResponse });
            Assert.AreSequenceEqual(new byte[] { 0x74, 0x61, 0x70, 0xff }, first.Identity.ToByteArray());
            Assert.AreSequenceEqual(new byte[] { 0, 0x31, 0 }, first.Version.ToByteArray());
            Assert.AreSame(first.Identity, second.Identity);
            Assert.AreSame(first.Version, second.Version);
            Assert.IsFalse(ReferenceEquals(first.Message, second.Message));
        }

        [TestMethod]
        public void EmptyMetadataIsNotSerialized()
        {
            var record = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery
            });
            Assert.IsTrue(record.Identity.IsEmpty);
            Assert.IsTrue(record.Version.IsEmpty);
            Assert.AreSequenceEqual(Convert.FromHexString("720208057801"), record.ToByteArray());
        }

        [TestMethod]
        public void ImmutableFactoryCanBeSharedAcrossConcurrentObservations()
        {
            byte[] identity = "producer"u8.ToArray();
            byte[] version = "0.2"u8.ToArray();
            var factory = new DnstapRecordFactory(identity, version);
            identity[0] = 0;
            version[0] = 0;
            var records = new Dnstap[256];
            Parallel.For(0, records.Length, i =>
            {
                records[i] = factory.Create(new DnsLogEntry
                {
                    EventType = Message.Types.Type.ClientQuery,
                    ClientPort = (ushort)i
                });
            });
            foreach (var (record, i) in records.Select((record, i) => (record, i)))
            {
                Assert.AreSequenceEqual("producer"u8.ToArray(), record.Identity.ToByteArray());
                Assert.AreSequenceEqual("0.2"u8.ToArray(), record.Version.ToByteArray());
                Assert.AreEqual((uint)i, record.Message.QueryPort);
                Assert.AreSame(records[0].Identity, record.Identity);
                Assert.AreSame(records[0].Version, record.Version);
            }
        }

        [TestMethod]
        public void FS027_DualTimestampsPreserveBothInstantsAndNanosecondPrecision()
        {
            var queryTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(1_234_567);
            var responseTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_001).AddTicks(2_345_678);
            var message = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientResponse,
                QueryTimestamp = queryTime,
                ResponseTimestamp = responseTime
            }).Message;
            Assert.AreEqual(1_700_000_000ul, message.QueryTimeSec);
            Assert.AreEqual(123_456_700u, message.QueryTimeNsec);
            Assert.AreEqual(1_700_000_001ul, message.ResponseTimeSec);
            Assert.AreEqual(234_567_800u, message.ResponseTimeNsec);
        }

        [TestMethod]
        public void FS027_LegacyTimestampMapsByEventAndCanCoexistWithOtherInstant()
        {
            var queryTime = DateTimeOffset.FromUnixTimeSeconds(100);
            var responseTime = DateTimeOffset.FromUnixTimeSeconds(90);
            var response = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientResponse,
                Timestamp = responseTime,
                QueryTimestamp = queryTime
            }).Message;
            Assert.AreEqual(100ul, response.QueryTimeSec);
            Assert.AreEqual(90ul, response.ResponseTimeSec);
            var query = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                Timestamp = queryTime,
                ResponseTimestamp = responseTime
            }).Message;
            Assert.AreEqual(100ul, query.QueryTimeSec);
            Assert.AreEqual(90ul, query.ResponseTimeSec);
        }

        [TestMethod]
        public void FS027_RejectsConflictingMappedTimestamps()
        {
            var time = DateTimeOffset.UnixEpoch;
            _ = Assert.ThrowsExactly<ArgumentException>(() => new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                Timestamp = time,
                QueryTimestamp = time
            }));
            _ = Assert.ThrowsExactly<ArgumentException>(() => new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientResponse,
                Timestamp = time,
                ResponseTimestamp = time
            }));
        }

        [TestMethod]
        public void FS027_RejectsPreEpochExplicitInstants()
        {
            var invalid = DateTimeOffset.UnixEpoch.AddTicks(-1);
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                QueryTimestamp = invalid
            }));
            _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientResponse,
                ResponseTimestamp = invalid
            }));
        }

        [TestMethod]
        public void FS028_QueryZonePreservesRawWireBytesIncludingRootAndBinaryLabel()
        {
            byte[] zone = [3, (byte)'w', 0, (byte)'x', 0];
            var result = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.AuthQuery,
                QueryZone = zone
            });
            zone[1] = 0xff;
            Assert.AreSequenceEqual(new byte[] { 3, (byte)'w', 0, (byte)'x', 0 },
                result.Message.QueryZone.ToByteArray());
            var root = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.AuthQuery,
                QueryZone = new byte[] { 0 }
            });
            Assert.AreSequenceEqual(new byte[] { 0 }, root.Message.QueryZone.ToByteArray());
        }

        [TestMethod]
        public void FS028_QueryZoneAcceptsMaxLengthUncompressedName()
        {
            byte[] zone = [63, .. new byte[63], 63, .. new byte[63],
                63, .. new byte[63], 61, .. new byte[61], 0];
            Assert.AreEqual(255, zone.Length);
            var record = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                QueryZone = zone
            });
            Assert.AreEqual(255, record.Message.QueryZone.Length);
        }

        [TestMethod]
        public void FS028_QueryZoneRejectsInvalidWireNamesAndText()
        {
            byte[][] invalid =
            [
                [(byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e'],
                [3, (byte)'f', (byte)'o', (byte)'o'],
                [0, 1, (byte)'x', 0],
                [0xc0, 0x00],
                [0x40, 0],
                [0x80, 0],
                [64, .. new byte[64], 0],
                [63, .. new byte[63], 63, .. new byte[63], 63, .. new byte[63],
                    62, .. new byte[62], 0]
            ];
            foreach (var zone in invalid)
            {
                _ = Assert.ThrowsExactly<ArgumentException>(() => new DnstapRecordFactory().Create(new DnsLogEntry
                {
                    EventType = Message.Types.Type.ClientQuery,
                    QueryZone = zone
                }));
            }
        }

        [TestMethod]
        public void FS028_EmptyQueryZoneOmitsProtobufField()
        {
            var record = new DnstapRecordFactory().Create(new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery
            });
            Assert.IsFalse(record.Message.HasQueryZone);
        }
    }
}
