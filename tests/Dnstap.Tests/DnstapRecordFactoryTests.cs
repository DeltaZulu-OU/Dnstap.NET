using System.Net;
using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class DnstapRecordFactoryTests
{
    [TestMethod]
    public void CreatesMetadataOnlyClientResponseWithoutInventingDnsPackets()
    {
        var client = IPAddress.Parse("192.0.2.10");
        var server = IPAddress.Parse("192.0.2.53");
        var record = DnstapRecordFactory.Create(new DnsLogEntry
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
        Assert.AreEqual(global::Dnstap.Dnstap.Types.Type.Message, record.Type);
        Assert.AreEqual(Message.Types.Type.ClientResponse, record.Message.Type);
        Assert.AreEqual(SocketFamily.Inet, record.Message.SocketFamily);
        CollectionAssert.AreEqual(client.GetAddressBytes(), record.Message.QueryAddress.ToByteArray());
        CollectionAssert.AreEqual(server.GetAddressBytes(), record.Message.ResponseAddress.ToByteArray());
        Assert.AreEqual(53000u, record.Message.QueryPort);
        Assert.AreEqual(53u, record.Message.ResponsePort);
        Assert.AreEqual(SocketProtocol.Udp, record.Message.SocketProtocol);
        Assert.AreEqual(1_700_000_000ul, record.Message.ResponseTimeSec);
        Assert.AreEqual(999_999_900u, record.Message.ResponseTimeNsec);
        Assert.IsFalse(record.Message.HasQueryTimeSec);
        Assert.IsFalse(record.Message.HasQueryTimeNsec);
        Assert.IsFalse(record.Message.HasQueryMessage);
        Assert.IsFalse(record.Message.HasResponseMessage);
        Assert.AreEqual(record, global::Dnstap.Dnstap.Parser.ParseFrom(record.ToByteArray()));
    }

    [TestMethod]
    public void CreatesQueryWithIpv6AndCopiesBinaryPackets()
    {
        byte[] packet = [0, 255, 128, 1];
        var timestamp = new DateTimeOffset(2026, 11, 15, 15, 30, 0, TimeSpan.FromHours(2)).AddTicks(1234);
        var record = DnstapRecordFactory.Create(new DnsLogEntry
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
        CollectionAssert.AreEqual(new byte[] { 0, 255, 128, 1 }, record.Message.QueryMessage.ToByteArray());
    }

    [TestMethod]
    public void AcceptsUnspecifiedOptionalMetadataWithoutSettingIt()
    {
        var record = DnstapRecordFactory.Create(new DnsLogEntry { EventType = Message.Types.Type.StubQuery });
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
        var message = DnstapRecordFactory.Create(new DnsLogEntry
        {
            EventType = type,
            Timestamp = DateTimeOffset.UnixEpoch.AddTicks(1)
        }).Message;
        Assert.AreEqual(type, message.Type);
        bool response = numericValue % 2 == 0;
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
        var record = DnstapRecordFactory.Create(new DnsLogEntry
        {
            EventType = Message.Types.Type.ClientQuery,
            Transport = protocol
        });
        Assert.AreEqual(protocol, record.Message.SocketProtocol);
    }

    [TestMethod]
    public void RejectsUnknownEventTypeAndSocketProtocol()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry { EventType = (Message.Types.Type)0 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry { EventType = (Message.Types.Type)99 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry { EventType = Message.Types.Type.ClientQuery, Transport = (SocketProtocol)99 }));
    }

    [TestMethod]
    public void RejectsMixedIpFamiliesAndScopedIpv6()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                ClientAddress = IPAddress.Parse("192.0.2.10"),
                ServerAddress = IPAddress.Parse("2001:db8::1")
            }));

        Assert.ThrowsExactly<ArgumentException>(() => DnstapRecordFactory.Create(
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
        var result = DnstapRecordFactory.Create(new DnsLogEntry
        {
            EventType = Message.Types.Type.ClientQuery,
            ClientAddress = mapped,
            ServerAddress = IPAddress.IPv6Loopback
        });
        Assert.AreEqual(SocketFamily.Inet6, result.Message.SocketFamily);
        CollectionAssert.AreEqual(mapped.GetAddressBytes(), result.Message.QueryAddress.ToByteArray());
    }

    [TestMethod]
    public void RejectsPreEpochTimestampAndNullEntry()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => DnstapRecordFactory.Create(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry
            {
                EventType = Message.Types.Type.ClientQuery,
                Timestamp = DateTimeOffset.UnixEpoch.AddTicks(-1)
            }));
    }

    [TestMethod]
    public void ExplicitZeroPortsRemainPresent()
    {
        var result = DnstapRecordFactory.Create(new DnsLogEntry
        {
            EventType = Message.Types.Type.ClientQuery,
            ClientPort = 0,
            ServerPort = ushort.MaxValue
        });
        Assert.IsTrue(result.Message.HasQueryPort);
        Assert.AreEqual(0u, result.Message.QueryPort);
        Assert.AreEqual((uint)ushort.MaxValue, result.Message.ResponsePort);
    }
}
