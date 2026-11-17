using System.Buffers.Binary;
using System.Net.Sockets;
using Dnstap;
using Google.Protobuf;

try
{
    if (args.Length == 0 || (args[0] != "file" && args[0] != "tcp" && args[0] != "unix"))
    {
        Console.Error.WriteLine("Usage: Tapper file --output PATH --count N [--no-complete] | Tapper tcp --host HOST --port PORT --count N [--no-complete] | Tapper unix --path SOCKET --count N [--no-complete]");
        return 2;
    }

    var mode = args[0];
    var noComplete = args.Contains("--no-complete", StringComparer.Ordinal);
    var expectedArgs = (mode == "tcp" ? 7 : 5) + (noComplete ? 1 : 0);
    if (args.Length != expectedArgs ||
        !int.TryParse(GetOption(args, "--count"), out var count) || count < 0)
    {
        throw new ArgumentException("Invalid arguments or count.");
    }

    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    if (mode == "file")
    {
        await using var output = new FileStream(GetOption(args, "--output"), FileMode.Create, FileAccess.Write);
        await EmitAsync(output, DnstapStreamMode.File, count, noComplete, deadline.Token);
    }
    else if (mode == "unix")
    {
        var path = GetOption(args, "--path");
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token);
        await using var output = new NetworkStream(client, ownsSocket: false);
        await EmitAsync(output, DnstapStreamMode.Duplex, count, noComplete, deadline.Token);
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
        await EmitAsync(client.GetStream(), DnstapStreamMode.Duplex, count, noComplete, deadline.Token);
    }

    Console.WriteLine($"Wrote {count} dnstap records.");
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

static async Task EmitAsync(Stream output, DnstapStreamMode mode, int count, bool noComplete, CancellationToken cancellationToken)
{
    await using var writer = new DnstapWriter(output, mode);
    await writer.StartAsync(cancellationToken);
    for (var i = 0; i < count; i++)
    {
        await writer.WriteAsync(CreateEvent(i), cancellationToken);
    }

    if (!noComplete)
    {
        await writer.CompleteAsync(cancellationToken);
    }
}

static Dnstap.Dnstap CreateEvent(int index)
{
    var query = Convert.FromHexString("000101000001000000000000076578616D706C6503636F6D0000010001");
    BinaryPrimitives.WriteUInt16BigEndian(query, (ushort)index);
    return new Dnstap.Dnstap
    {
        Type = Dnstap.Dnstap.Types.Type.Message,
        Identity = ByteString.CopyFromUtf8("tapper"),
        Message = new Message
        {
            Type = Message.Types.Type.ClientQuery,
            SocketFamily = SocketFamily.Inet,
            SocketProtocol = SocketProtocol.Udp,
            QueryAddress = ByteString.CopyFrom(new byte[] { 192, 0, 2, 10 }),
            ResponseAddress = ByteString.CopyFrom(new byte[] { 192, 0, 2, 53 }),
            QueryPort = (uint)(53000 + index % 10000),
            ResponsePort = 53,
            QueryTimeSec = 1_700_000_000,
            QueryTimeNsec = (uint)(index % 1_000_000_000),
            QueryMessage = ByteString.CopyFrom(query)
        }
    };
}
