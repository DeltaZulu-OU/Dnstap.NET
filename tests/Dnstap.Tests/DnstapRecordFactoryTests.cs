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
        var record = DnstapRecordFactory.Create(new DnsLogEntry
        {
            EventType = "client_response",
            ClientAddress = "192.0.2.10",
            ServerAddress = "192.0.2.53",
            ClientPort = "53000",
            ServerPort = "53",
            Transport = "udp",
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(9_999_999)
        });

        Assert.IsTrue(record.IsInitialized());
        Assert.AreEqual(global::Dnstap.Dnstap.Types.Type.Message, record.Type);
        Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientResponse, record.Message.Type);
        Assert.AreEqual(SocketFamily.Inet, record.Message.SocketFamily);
        CollectionAssert.AreEqual(IPAddress.Parse("192.0.2.10").GetAddressBytes(), record.Message.QueryAddress.ToByteArray());
        CollectionAssert.AreEqual(IPAddress.Parse("192.0.2.53").GetAddressBytes(), record.Message.ResponseAddress.ToByteArray());
        Assert.AreEqual(53000u, record.Message.QueryPort);
        Assert.AreEqual(53u, record.Message.ResponsePort);
        Assert.AreEqual(SocketProtocol.Udp, record.Message.SocketProtocol);
        Assert.AreEqual(1_700_000_000ul, record.Message.ResponseTimeSec);
        Assert.AreEqual(999_999_900u, record.Message.ResponseTimeNsec);
        Assert.IsFalse(record.Message.HasQueryTimeSec);
        Assert.IsFalse(record.Message.HasQueryTimeNsec);
        Assert.IsFalse(record.Message.HasQueryMessage);
        Assert.IsFalse(record.Message.HasResponseMessage);

        var parsed = global::Dnstap.Dnstap.Parser.ParseFrom(record.ToByteArray());
        Assert.AreEqual(record, parsed);
    }

    [TestMethod]
    public void CreatesQueryWithIpv6AndCopiesBinaryPackets()
    {
        byte[] packet = [0, 255, 128, 1];
        var timestamp = new DateTimeOffset(2026, 11, 15, 15, 30, 0, TimeSpan.FromHours(2)).AddTicks(1234);
        var record = DnstapRecordFactory.Create(new DnsLogEntry
        {
            EventType = "RESOLVER_QUERY",
            ClientAddress = "2001:db8::1",
            ServerAddress = "2001:db8::53",
            Transport = "DOQ",
            Timestamp = timestamp,
            QueryPacket = packet
        });
        packet[1] = 0; // Caller may reuse the packet allocation.

        Assert.AreEqual(global::Dnstap.Message.Types.Type.ResolverQuery, record.Message.Type);
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
        var record = DnstapRecordFactory.Create(new DnsLogEntry { EventType = "STUB_QUERY" });
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
    [DataRow("AUTH_QUERY", 1)]
    [DataRow("AUTH_RESPONSE", 2)]
    [DataRow("RESOLVER_QUERY", 3)]
    [DataRow("RESOLVER_RESPONSE", 4)]
    [DataRow("CLIENT_QUERY", 5)]
    [DataRow("CLIENT_RESPONSE", 6)]
    [DataRow("FORWARDER_QUERY", 7)]
    [DataRow("FORWARDER_RESPONSE", 8)]
    [DataRow("STUB_QUERY", 9)]
    [DataRow("STUB_RESPONSE", 10)]
    [DataRow("TOOL_QUERY", 11)]
    [DataRow("TOOL_RESPONSE", 12)]
    [DataRow("UPDATE_QUERY", 13)]
    [DataRow("UPDATE_RESPONSE", 14)]
    public void MapsEveryOfficialEventTypeAndTimestampDirection(string type, int expected)
    {
        var instant = DateTimeOffset.UnixEpoch.AddTicks(1);
        var message = DnstapRecordFactory.Create(new DnsLogEntry { EventType = type, Timestamp = instant }).Message;
        Assert.AreEqual(expected, (int)message.Type);
        if (type.EndsWith("_RESPONSE", StringComparison.Ordinal))
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
    [DataRow("UDP", 1)]
    [DataRow("TCP", 2)]
    [DataRow("DOT", 3)]
    [DataRow("DOH", 4)]
    [DataRow("DNSCryptUDP", 5)]
    [DataRow("DNSCryptTCP", 6)]
    [DataRow("DOQ", 7)]
    public void MapsEveryOfficialSocketProtocol(string transport, int expected)
    {
        var message = DnstapRecordFactory.Create(new DnsLogEntry
        {
            EventType = "CLIENT_QUERY",
            Transport = transport
        }).Message;
        Assert.AreEqual(expected, (int)message.SocketProtocol);
    }

    [TestMethod]
    public void RejectsUnknownEventType()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry { EventType = "BAD_EVENT" }));
    }

    [TestMethod]
    public void RejectsInvalidTransportAndPorts()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry { EventType = "CLIENT_QUERY", Transport = "TLS" }));
        foreach (string invalid in new[] { "-1", "65536", "12x", " 53 ", "" })
            Assert.ThrowsExactly<ArgumentException>(() => DnstapRecordFactory.Create(
                new DnsLogEntry { EventType = "CLIENT_QUERY", ClientPort = invalid }));
    }

    [TestMethod]
    public void RejectsInvalidOrMixedIpAddresses()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry { EventType = "CLIENT_QUERY", ClientAddress = "999.999.999.999" }));
        Assert.ThrowsExactly<ArgumentException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry { EventType = "CLIENT_QUERY", ClientAddress = "192.0.2.10", ServerAddress = "2001:db8::1" }));
    }

    [TestMethod]
    public void RejectsPreEpochTimestampAndNullEntry()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => DnstapRecordFactory.Create(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DnstapRecordFactory.Create(
            new DnsLogEntry { EventType = "CLIENT_QUERY", Timestamp = DateTimeOffset.UnixEpoch.AddTicks(-1) }));
    }
}
