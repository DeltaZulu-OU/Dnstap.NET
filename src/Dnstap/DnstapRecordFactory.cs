using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

namespace Dnstap;

/// <summary>Converts string-valued DNS log metadata into the pinned official dnstap protobuf model.</summary>
public static class DnstapRecordFactory
{
    public static global::Dnstap.Dnstap Create(DnsLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var eventType = ParseEventType(entry.EventType);
        var message = new global::Dnstap.Message { Type = eventType };

        // dnstap query_address is the initiator; response_address is the responder.
        // Both must have the same socket family when provided.
        IPAddress? initiator = ParseAddress(entry.ClientAddress, nameof(entry.ClientAddress));
        IPAddress? responder = ParseAddress(entry.ServerAddress, nameof(entry.ServerAddress));
        if (initiator is not null && responder is not null
            && initiator.AddressFamily != responder.AddressFamily)
            throw new ArgumentException("ClientAddress and ServerAddress must use the same IP family.", nameof(entry));

        var address = initiator ?? responder;
        if (address is not null)
            message.SocketFamily = address.AddressFamily == AddressFamily.InterNetwork
                ? SocketFamily.Inet : SocketFamily.Inet6;
        if (initiator is not null)
            message.QueryAddress = ByteString.CopyFrom(initiator.GetAddressBytes());
        if (responder is not null)
            message.ResponseAddress = ByteString.CopyFrom(responder.GetAddressBytes());

        if (entry.ClientPort is not null)
            message.QueryPort = ParsePort(entry.ClientPort, nameof(entry.ClientPort));
        if (entry.ServerPort is not null)
            message.ResponsePort = ParsePort(entry.ServerPort, nameof(entry.ServerPort));
        if (entry.Transport is not null)
            message.SocketProtocol = ParseTransport(entry.Transport);

        if (entry.Timestamp is { } timestamp)
        {
            long seconds = timestamp.ToUnixTimeSeconds();
            if (seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(entry), "Dnstap timestamps must not precede the Unix epoch.");

            // DateTimeOffset.UtcTicks is measured in 100ns intervals.
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

    private static global::Dnstap.Message.Types.Type ParseEventType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("EventType is required.", nameof(value));

        return value.Trim().ToUpperInvariant() switch
        {
            "AUTH_QUERY" => global::Dnstap.Message.Types.Type.AuthQuery,
            "AUTH_RESPONSE" => global::Dnstap.Message.Types.Type.AuthResponse,
            "RESOLVER_QUERY" => global::Dnstap.Message.Types.Type.ResolverQuery,
            "RESOLVER_RESPONSE" => global::Dnstap.Message.Types.Type.ResolverResponse,
            "CLIENT_QUERY" => global::Dnstap.Message.Types.Type.ClientQuery,
            "CLIENT_RESPONSE" => global::Dnstap.Message.Types.Type.ClientResponse,
            "FORWARDER_QUERY" => global::Dnstap.Message.Types.Type.ForwarderQuery,
            "FORWARDER_RESPONSE" => global::Dnstap.Message.Types.Type.ForwarderResponse,
            "STUB_QUERY" => global::Dnstap.Message.Types.Type.StubQuery,
            "STUB_RESPONSE" => global::Dnstap.Message.Types.Type.StubResponse,
            "TOOL_QUERY" => global::Dnstap.Message.Types.Type.ToolQuery,
            "TOOL_RESPONSE" => global::Dnstap.Message.Types.Type.ToolResponse,
            "UPDATE_QUERY" => global::Dnstap.Message.Types.Type.UpdateQuery,
            "UPDATE_RESPONSE" => global::Dnstap.Message.Types.Type.UpdateResponse,
            _ => throw new ArgumentException($"Unknown dnstap event type: '{value}'.", nameof(value))
        };
    }

    private static bool IsResponse(global::Dnstap.Message.Types.Type type) =>
        type is global::Dnstap.Message.Types.Type.AuthResponse
            or global::Dnstap.Message.Types.Type.ResolverResponse
            or global::Dnstap.Message.Types.Type.ClientResponse
            or global::Dnstap.Message.Types.Type.ForwarderResponse
            or global::Dnstap.Message.Types.Type.StubResponse
            or global::Dnstap.Message.Types.Type.ToolResponse
            or global::Dnstap.Message.Types.Type.UpdateResponse;

    private static IPAddress? ParseAddress(string? value, string name)
    {
        if (value is null)
            return null;
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("Expected a valid IPv4 or IPv6 address.", name);
        return address;
    }

    private static uint ParsePort(string value, string name)
    {
        if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port))
            throw new ArgumentException("Expected an unsigned port number from 0 to 65535.", name);
        return port;
    }

    private static SocketProtocol ParseTransport(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "UDP" => SocketProtocol.Udp,
            "TCP" => SocketProtocol.Tcp,
            "DOT" => SocketProtocol.Dot,
            "DOH" => SocketProtocol.Doh,
            "DOQ" => SocketProtocol.Doq,
            // These values are defined by the pinned official proto (5 and 6).
            "DNSCRYPTUDP" => (SocketProtocol)5,
            "DNSCRYPTTCP" => (SocketProtocol)6,
            _ => throw new ArgumentException($"Unknown dnstap transport: '{value}'.", nameof(value))
        };
}
