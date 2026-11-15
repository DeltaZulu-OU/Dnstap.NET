using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

namespace Dnstap;

/// <summary>Maps native .NET DNS event metadata into the official dnstap protobuf model.</summary>
public static class DnstapRecordFactory
{
    public static global::Dnstap.Dnstap Create(DnsLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var eventType = entry.EventType;
        if (!Enum.IsDefined(eventType))
            throw new ArgumentOutOfRangeException(nameof(DnsLogEntry.EventType), "Unknown dnstap event type.");

        var message = new Message { Type = eventType };

        // dnstap query_address is the initiator; response_address is the responder.
        IPAddress? initiator = ValidateAddress(entry.ClientAddress, nameof(DnsLogEntry.ClientAddress));
        IPAddress? responder = ValidateAddress(entry.ServerAddress, nameof(DnsLogEntry.ServerAddress));
        if (initiator is not null && responder is not null
            && initiator.AddressFamily != responder.AddressFamily)
            throw new ArgumentException("ClientAddress and ServerAddress must use the same IP family.", nameof(entry));

        IPAddress? address = initiator ?? responder;
        if (address is not null)
            message.SocketFamily = address.AddressFamily == AddressFamily.InterNetwork
                ? SocketFamily.Inet : SocketFamily.Inet6;
        if (initiator is not null)
            message.QueryAddress = ByteString.CopyFrom(initiator.GetAddressBytes());
        if (responder is not null)
            message.ResponseAddress = ByteString.CopyFrom(responder.GetAddressBytes());

        if (entry.ClientPort is { } clientPort)
            message.QueryPort = clientPort;
        if (entry.ServerPort is { } serverPort)
            message.ResponsePort = serverPort;

        if (entry.Transport is { } transport)
        {
            if (!Enum.IsDefined(transport))
                throw new ArgumentOutOfRangeException(nameof(DnsLogEntry.Transport), "Unknown dnstap socket protocol.");
            message.SocketProtocol = transport;
        }

        if (entry.Timestamp is { } timestamp)
        {
            long seconds = timestamp.ToUnixTimeSeconds();
            if (seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(DnsLogEntry.Timestamp), "Dnstap timestamps must not precede the Unix epoch.");

            uint nanoseconds = checked((uint)((timestamp.UtcTicks % TimeSpan.TicksPerSecond) * 100));
            if (IsResponse(eventType))
            {
                message.ResponseTimeSec = (ulong)seconds;
                message.ResponseTimeNsec = nanoseconds;
            }
            else
            {
                message.QueryTimeSec = (ulong)seconds;
                message.QueryTimeNsec = nanoseconds;
            }
        }

        if (!entry.QueryPacket.IsEmpty)
            message.QueryMessage = ByteString.CopyFrom(entry.QueryPacket.Span);
        if (!entry.ResponsePacket.IsEmpty)
            message.ResponseMessage = ByteString.CopyFrom(entry.ResponsePacket.Span);

        return new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = message
        };
    }

    private static bool IsResponse(Message.Types.Type type) =>
        type is Message.Types.Type.AuthResponse
            or Message.Types.Type.ResolverResponse
            or Message.Types.Type.ClientResponse
            or Message.Types.Type.ForwarderResponse
            or Message.Types.Type.StubResponse
            or Message.Types.Type.ToolResponse
            or Message.Types.Type.UpdateResponse;

    private static IPAddress? ValidateAddress(IPAddress? address, string property)
    {
        if (address is null)
            return null;
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("Expected an IPv4 or IPv6 address.", property);
        // The dnstap schema stores address bytes only; it has no scope-ID field.
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            throw new ArgumentException("Scoped IPv6 addresses cannot be represented without losing the scope ID.", property);
        return address;
    }

}
