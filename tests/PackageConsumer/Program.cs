using System.Net;
using Dnstap;
using Google.Protobuf;

var client = IPAddress.Parse("192.0.2.10");
var server = IPAddress.Parse("192.0.2.53");
byte[] dnsPacket = Convert.FromHexString("000101000001000000000000076578616D706C6503636F6D0000010001");

var factory = new DnstapRecordFactory("external-consumer"u8.ToArray(), "test"u8.ToArray());
var record = factory.Create(new DnsLogEntry
{
    EventType = Message.Types.Type.ClientQuery,
    ClientAddress = client,
    ServerAddress = server,
    ClientPort = 53000,
    ServerPort = 53,
    Transport = SocketProtocol.Udp,
    Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
    QueryPacket = dnsPacket
});

if (!record.Identity.Span.SequenceEqual("external-consumer"u8) ||
    !record.Version.Span.SequenceEqual("test"u8))
    throw new InvalidOperationException("Packaged factory did not preserve producer metadata.");
if (!record.IsInitialized() || record.Message.Type != Message.Types.Type.ClientQuery)
    throw new InvalidOperationException("Packaged record factory did not generate a valid event.");
if (!record.Message.QueryAddress.Span.SequenceEqual(client.GetAddressBytes()))
    throw new InvalidOperationException("Packaged factory did not preserve the client address.");
if (!record.Message.QueryMessage.Span.SequenceEqual(dnsPacket))
    throw new InvalidOperationException("Packaged factory did not preserve DNS wire bytes.");

var parsed = global::Dnstap.Dnstap.Parser.ParseFrom(record.ToByteArray());
if (!parsed.Equals(record))
    throw new InvalidOperationException("Packaged protobuf record failed round-trip.");

await using var destination = new MemoryStream();
await using (var writer = new DnstapWriter(destination))
{
    await writer.StartAsync();
    await writer.WriteAsync(record);
    await writer.CompleteAsync();
}
if (destination.Length == 0)
    throw new InvalidOperationException("Packaged writer produced no frame-stream output.");

Console.WriteLine("Independent package consumer passed: factory, protobuf and file writer.");
