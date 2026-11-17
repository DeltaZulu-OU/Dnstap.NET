using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Net.Sockets;
using Dnstap;
using Google.Protobuf;

try
{
    if (args.Length == 0 || (args[0] != "file" && args[0] != "tcp" && args[0] != "unix"))
    {
        Console.Error.WriteLine("Usage: Tapper file --output PATH --count N [--response-timestamps] [--no-complete] | Tapper tcp --host HOST --port PORT --count N [--no-complete] | Tapper unix --path SOCKET --count N [--no-complete]");
        return 2;
    }

    var mode = args[0];
    var noComplete = args.Contains("--no-complete", StringComparer.Ordinal);
    var responseTimestamps = args.Contains("--response-timestamps", StringComparer.Ordinal);
    var expectedArgs = (mode == "tcp" ? 7 : 5) + (noComplete ? 1 : 0) + (responseTimestamps ? 1 : 0);
    if (responseTimestamps && mode != "file")
    {
        throw new ArgumentException("--response-timestamps is supported only in file mode.");
    }
    if (args.Length != expectedArgs ||
        !int.TryParse(GetOption(args, "--count"), out var count) || count < 0)
    {
        throw new ArgumentException("Invalid arguments or count.");
    }

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    if (mode == "file")
    {
        await using var output = new FileStream(GetOption(args, "--output"), FileMode.Create, FileAccess.Write);
        await EmitAsync(output, DnstapStreamMode.File, count, noComplete, responseTimestamps, deadline.Token);
    }
    else if (mode == "unix")
    {
        var path = GetOption(args, "--path");
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token);
        await using var output = new NetworkStream(client, ownsSocket: false);
        await EmitAsync(output, DnstapStreamMode.Duplex, count, noComplete, responseTimestamps, deadline.Token);
    }
    else
    {
        var host = GetOption(args, "--host");
        if (!int.TryParse(GetOption(args, "--port"), out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("Invalid port.");
        }

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, deadline.Token);
        await EmitAsync(client.GetStream(), DnstapStreamMode.Duplex, count, noComplete, responseTimestamps, deadline.Token);
    }

    Console.WriteLine($"Wrote {count + (responseTimestamps ? 1 : 0)} dnstap records.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static string GetOption(string[] arguments, string option)
{
    for (var i = 1; i < arguments.Length - 1; i++)
    {
        if (arguments[i] == option && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return arguments[i + 1];
        }
    }

    throw new ArgumentException($"Missing {option}.");
}

static async Task EmitAsync(Stream output, DnstapStreamMode mode, int count, bool noComplete, bool responseTimestamps, CancellationToken cancellationToken)
{
    var factory = new DnstapRecordFactory(Encoding.UTF8.GetBytes("tapper"));
    await using var writer = new DnstapWriter(output, mode);
    await writer.StartAsync(cancellationToken);
    for (var i = 0; i < count; i++)
    {
        await writer.WriteAsync(CreateEvent(i, factory), cancellationToken);
    }

    if (responseTimestamps)
    {
        var query = Convert.FromHexString("000101000001000000000000076578616D706C6503636F6D0000010001");
        var response = Convert.FromHexString("000181800001000000000000076578616D706C6503636F6D0000010001");
        await writer.WriteAsync(factory.Create(new DnsLogEntry
        {
            EventType = Message.Types.Type.ClientResponse,
            ClientAddress = IPAddress.Parse("192.0.2.10"),
            ServerAddress = IPAddress.Parse("192.0.2.53"),
            ClientPort = 53000,
            ServerPort = 53,
            Transport = SocketProtocol.Udp,
            QueryTimestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(1_234_567),
            ResponseTimestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_001).AddTicks(2_345_678),
            QueryPacket = query,
            ResponsePacket = response
        }), cancellationToken);
    }

    if (!noComplete)
    {
        await writer.CompleteAsync(cancellationToken);
    }
}

static Dnstap.Dnstap CreateEvent(int index, DnstapRecordFactory factory)
{
    var query = Convert.FromHexString("000101000001000000000000076578616D706C6503636F6D0000010001");
    BinaryPrimitives.WriteUInt16BigEndian(query, (ushort)index);
    return factory.Create(new DnsLogEntry
    {
        EventType = Message.Types.Type.ClientQuery,
        ClientAddress = IPAddress.Parse("192.0.2.10"),
        ServerAddress = IPAddress.Parse("192.0.2.53"),
        ClientPort = (ushort)(53000 + index % 10000),
        ServerPort = 53,
        Transport = SocketProtocol.Udp,
        Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(index),
        QueryPacket = query
    });
}
