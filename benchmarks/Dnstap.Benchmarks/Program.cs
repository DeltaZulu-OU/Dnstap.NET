using Dnstap;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Google.Protobuf;

BenchmarkSwitcher.FromAssembly(typeof(FileWriterBenchmarks).Assembly).Run(args);

public abstract class WriterBenchmarkBase
{
    [Params(64, 1024, 8192)]
    public int PayloadBytes { get; set; }

    protected global::Dnstap.Dnstap Message = null!;

    protected void PrepareMessage() =>
        Message = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message
            {
                Type = global::Dnstap.Message.Types.Type.ClientQuery,
                QueryMessage = ByteString.CopyFrom(new byte[PayloadBytes])
            }
        };
}

[MemoryDiagnoser]
public class FileWriterBenchmarks : WriterBenchmarkBase
{
    private DnstapWriter _writer = null!;

    [GlobalSetup]
    public void Setup()
    {
        PrepareMessage();
        _writer = new DnstapWriter(Stream.Null);
        _writer.StartAsync().GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task WriteFrame() => _writer.WriteAsync(Message);

    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.CompleteAsync().GetAwaiter().GetResult();
        _writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

[MemoryDiagnoser]
public class LoopbackTcpWriterBenchmarks : WriterBenchmarkBase
{
    private TcpListener _listener = null!;
    private TcpClient _client = null!;
    private Task _receiver = null!;
    private DnstapWriter _writer = null!;

    [GlobalSetup]
    public void Setup()
    {
        PrepareMessage();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _receiver = Task.Run(async () =>
        {
            using TcpClient peer = await _listener.AcceptTcpClientAsync();
            using NetworkStream stream = peer.GetStream();
            await ReadControlAsync(stream); // READY
            await stream.WriteAsync(Convert.FromHexString("000000000000000400000001")); // ACCEPT
            await ReadControlAsync(stream); // START
            var header = new byte[4];
            byte[] recordBuffer = new byte[Message.CalculateSize()];
            while (true)
            {
                await stream.ReadExactlyAsync(header);
                uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (size == 0)
                {
                    await stream.ReadExactlyAsync(header);
                    int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
                    byte[] control = new byte[length];
                    await stream.ReadExactlyAsync(control);
                    if (BinaryPrimitives.ReadUInt32BigEndian(control) == 3) // STOP
                    {
                        await stream.WriteAsync(Convert.FromHexString("000000000000000400000005"));
                        return;
                    }
                }
                else
                {
                    if (size > (uint)recordBuffer.Length)
                        throw new InvalidDataException("DATA frame exceeded benchmark receiver buffer.");
                    await stream.ReadExactlyAsync(recordBuffer.AsMemory(0, checked((int)size)));
                }
            }
        });
        _client = new TcpClient();
        _client.Connect(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        _writer = new DnstapWriter(_client.GetStream(), DnstapStreamMode.Duplex);
        _writer.StartAsync().GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task WriteFrame() => _writer.WriteAsync(Message);

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            _writer.CompleteAsync().GetAwaiter().GetResult();
            _writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _receiver.GetAwaiter().GetResult();
        }
        finally
        {
            _client.Dispose();
            _listener.Stop();
        }
    }

    private static async Task ReadControlAsync(Stream stream)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0)
            throw new InvalidDataException("Expected a control frame.");
        byte[] payload = new byte[checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)))];
        await stream.ReadExactlyAsync(payload);
    }
}
