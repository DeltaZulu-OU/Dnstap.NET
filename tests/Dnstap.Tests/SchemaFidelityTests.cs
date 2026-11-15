using Google.Protobuf;

namespace Dnstap.Tests
{
    [TestClass]
    public sealed class SchemaFidelityTests
    {
        [TestMethod]
        public void GeneratedModelPreservesBinaryDnsPacketAndSingleInstantTimestamp()
        {
            // The input contains arbitrary bytes, not a text DNS name.
            byte[] packet = [0, 255, 128, 1, 0, 0, 0, 0];
            var instant = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)
                .AddTicks(9_999_999);
            var seconds = checked((ulong)instant.ToUnixTimeSeconds());
            var nanoseconds = checked((uint)((instant.UtcTicks % TimeSpan.TicksPerSecond) * 100));
            Assert.AreEqual(999_999_900u, nanoseconds);

            var original = new Dnstap
            {
                Type = Dnstap.Types.Type.Message,
                Message = new Message
                {
                    Type = Message.Types.Type.ClientQuery,
                    QueryMessage = ByteString.CopyFrom(packet),
                    QueryTimeSec = seconds,
                    QueryTimeNsec = nanoseconds
                }
            };
            var wire = original.ToByteArray();
            var parsed = Dnstap.Parser.ParseFrom(wire);

            Assert.AreEqual(Dnstap.Types.Type.Message, parsed.Type);
            Assert.AreEqual(Message.Types.Type.ClientQuery, parsed.Message.Type);
            Assert.AreSequenceEqual(packet, parsed.Message.QueryMessage.ToByteArray());
            Assert.AreEqual(seconds, parsed.Message.QueryTimeSec);
            Assert.AreEqual(nanoseconds, parsed.Message.QueryTimeNsec);
        }

        [TestMethod]
        public void OfficialGeneratedSchemaSupportsBinaryResponseAndPolicyMetadata()
        {
            byte[] response = [255, 0, 1, 128];
            var record = new Dnstap
            {
                Type = Dnstap.Types.Type.Message,
                Message = new Message
                {
                    Type = Message.Types.Type.ClientResponse,
                    ResponseMessage = ByteString.CopyFrom(response),
                    Policy = new Policy
                    {
                        Type = "RPZ",
                        Action = Policy.Types.Action.Nxdomain
                    }
                }
            };
            var parsed = Dnstap.Parser.ParseFrom(record.ToByteArray());
            Assert.AreSequenceEqual(response, parsed.Message.ResponseMessage.ToByteArray());
            Assert.AreEqual("RPZ", parsed.Message.Policy.Type);
            Assert.AreEqual(Policy.Types.Action.Nxdomain, parsed.Message.Policy.Action);
        }
    }
}
