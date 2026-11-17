using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

namespace Dnstap
{
    /// <summary>Maps native .NET DNS event metadata into the official dnstap protobuf model.</summary>
    public sealed class DnstapRecordFactory
    {
        private readonly ByteString _identity;
        private readonly ByteString _version;

        /// <summary>Captures stable producer metadata once. Empty values are omitted.</summary>
        public DnstapRecordFactory(ReadOnlyMemory<byte> identity = default, ReadOnlyMemory<byte> version = default)
        {
            _identity = identity.IsEmpty ? ByteString.Empty : ByteString.CopyFrom(identity.Span);
            _version = version.IsEmpty ? ByteString.Empty : ByteString.CopyFrom(version.Span);
        }

        /// <summary>Creates a new independent observation using immutable producer metadata.</summary>
        public Dnstap Create(DnsLogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var eventType = entry.EventType;
            if (!Enum.IsDefined(eventType))
            {
                throw new ArgumentOutOfRangeException(nameof(DnsLogEntry.EventType), "Unknown dnstap event type.");
            }

            var message = new Message { Type = eventType };

            // dnstap query_address is the initiator; response_address is the responder.
            var initiator = ValidateAddress(entry.ClientAddress, nameof(DnsLogEntry.ClientAddress));
            var responder = ValidateAddress(entry.ServerAddress, nameof(DnsLogEntry.ServerAddress));
            if (initiator is not null && responder is not null
                && initiator.AddressFamily != responder.AddressFamily)
            {
                throw new ArgumentException("ClientAddress and ServerAddress must use the same IP family.", nameof(entry));
            }

            var address = initiator ?? responder;
            if (address is not null)
            {
                message.SocketFamily = address.AddressFamily == AddressFamily.InterNetwork
                    ? SocketFamily.Inet : SocketFamily.Inet6;
            }

            if (initiator is not null)
            {
                message.QueryAddress = ByteString.CopyFrom(initiator.GetAddressBytes());
            }

            if (responder is not null)
            {
                message.ResponseAddress = ByteString.CopyFrom(responder.GetAddressBytes());
            }

            if (entry.ClientPort is { } clientPort)
            {
                message.QueryPort = clientPort;
            }

            if (entry.ServerPort is { } serverPort)
            {
                message.ResponsePort = serverPort;
            }

            if (entry.Transport is { } transport)
            {
                if (!Enum.IsDefined(transport))
                {
                    throw new ArgumentOutOfRangeException(nameof(DnsLogEntry.Transport), "Unknown dnstap socket protocol.");
                }

                message.SocketProtocol = transport;
            }

            var queryTimestamp = entry.QueryTimestamp;
            var responseTimestamp = entry.ResponseTimestamp;
            if (entry.Timestamp is { } shortcut)
            {
                if (IsResponse(eventType))
                {
                    if (responseTimestamp is not null)
                    {
                        throw new ArgumentException("Timestamp conflicts with ResponseTimestamp for a response event.", nameof(entry));
                    }
                    responseTimestamp = shortcut;
                }
                else
                {
                    if (queryTimestamp is not null)
                    {
                        throw new ArgumentException("Timestamp conflicts with QueryTimestamp for a query event.", nameof(entry));
                    }
                    queryTimestamp = shortcut;
                }
            }

            if (queryTimestamp is { } queryTime)
            {
                var (seconds, nanoseconds) = ConvertTimestamp(queryTime, nameof(DnsLogEntry.QueryTimestamp));
                message.QueryTimeSec = seconds;
                message.QueryTimeNsec = nanoseconds;
            }

            if (responseTimestamp is { } responseTime)
            {
                var (seconds, nanoseconds) = ConvertTimestamp(responseTime, nameof(DnsLogEntry.ResponseTimestamp));
                message.ResponseTimeSec = seconds;
                message.ResponseTimeNsec = nanoseconds;
            }

            if (!entry.QueryPacket.IsEmpty)
            {
                message.QueryMessage = ByteString.CopyFrom(entry.QueryPacket.Span);
            }

            if (!entry.ResponsePacket.IsEmpty)
            {
                message.ResponseMessage = ByteString.CopyFrom(entry.ResponsePacket.Span);
            }

            var record = new Dnstap
            {
                Type = Dnstap.Types.Type.Message,
                Message = message
            };
            if (!_identity.IsEmpty)
            {
                record.Identity = _identity;
            }
            if (!_version.IsEmpty)
            {
                record.Version = _version;
            }
            return record;
        }

        private static (ulong Seconds, uint Nanoseconds) ConvertTimestamp(DateTimeOffset timestamp, string property)
        {
            var seconds = timestamp.ToUnixTimeSeconds();
            if (seconds < 0)
            {
                throw new ArgumentOutOfRangeException(property, "Dnstap timestamps must not precede the Unix epoch.");
            }
            return ((ulong)seconds, checked((uint)((timestamp.UtcTicks % TimeSpan.TicksPerSecond) * 100)));
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
            {
                return null;
            }

            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                throw new ArgumentException("Expected an IPv4 or IPv6 address.", property);
            }
            // The dnstap schema stores address bytes only; it has no scope-ID field.
            return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
                ? throw new ArgumentException("Scoped IPv6 addresses cannot be represented without losing the scope ID.", property)
                : address;
        }

    }
}
