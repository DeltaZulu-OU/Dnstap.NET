using Google.Protobuf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class SchemaFidelityTests
{
    [TestMethod]
    public void GeneratedModelPreservesBinaryDnsPacketAndSingleInstantTimestamp()
    {
        // The input contains arbitrary bytes, not a text DNS name.
        byte[] packet = [0, 255, 128, 1, 0, 0, 0, 0];
        DateTimeOffset instant = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)
            .AddTicks(9_999_999);
        ulong seconds = checked((ulong)instant.ToUnixTimeSeconds());
        uint nanoseconds = checked((uint)((instant.UtcTicks % TimeSpan.TicksPerSecond) * 100));
        Assert.AreEqual(999_999_900u, nanoseconds);

        var original = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message
            {
                Type = global::Dnstap.Message.Types.Type.ClientQuery,
                QueryMessage = ByteString.CopyFrom(packet),
                QueryTimeSec = seconds,
                QueryTimeNsec = nanoseconds
            }
        };
        byte[] wire = original.ToByteArray();
        var parsed = global::Dnstap.Dnstap.Parser.ParseFrom(wire);

        Assert.AreEqual(global::Dnstap.Dnstap.Types.Type.Message, parsed.Type);
        Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientQuery, parsed.Message.Type);
        CollectionAssert.AreEqual(packet, parsed.Message.QueryMessage.ToByteArray());
        Assert.AreEqual(seconds, parsed.Message.QueryTimeSec);
        Assert.AreEqual(nanoseconds, parsed.Message.QueryTimeNsec);
    }

    [TestMethod]
    public void OfficialGeneratedSchemaSupportsBinaryResponseAndPolicyMetadata()
    {
        byte[] response = [255, 0, 1, 128];
        var record = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message
            {
                Type = global::Dnstap.Message.Types.Type.ClientResponse,
                ResponseMessage = ByteString.CopyFrom(response),
                Policy = new global::Dnstap.Policy
                {
                    Type = "RPZ",
                    Action = global::Dnstap.Policy.Types.Action.Nxdomain
                }
            }
        };
        var parsed = global::Dnstap.Dnstap.Parser.ParseFrom(record.ToByteArray());
        CollectionAssert.AreEqual(response, parsed.Message.ResponseMessage.ToByteArray());
        Assert.AreEqual("RPZ", parsed.Message.Policy.Type);
        Assert.AreEqual(global::Dnstap.Policy.Types.Action.Nxdomain, parsed.Message.Policy.Action);
    }
}
