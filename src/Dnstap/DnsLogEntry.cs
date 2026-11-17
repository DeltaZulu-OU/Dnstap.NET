using System.Net;

namespace Dnstap
{
    /// <summary>
    /// Typed DNS event metadata used to build a dnstap observation.
    /// ClientAddress/ClientPort identify the DNS message initiator (dnstap query_*),
    /// while ServerAddress/ServerPort identify the responder (dnstap response_*).
    /// These are wire roles, not necessarily the machine producing the observation.
    /// </summary>
    public sealed record DnsLogEntry
    {
        /// <summary>Official dnstap observation type, including the observer perspective.</summary>
        public required Message.Types.Type EventType { get; init; }

        /// <summary>Message initiator, if known. The caller owns address parsing.</summary>
        public IPAddress? ClientAddress { get; init; }
        /// <summary>Message responder, if known. The caller owns address parsing.</summary>
        public IPAddress? ServerAddress { get; init; }
        public ushort? ClientPort { get; init; }
        public ushort? ServerPort { get; init; }

        /// <summary>Official dnstap socket transport enum, if known.</summary>
        public SocketProtocol? Transport { get; init; }

        /// <summary>
        /// The observed query or response instant, mapped according to EventType.
        /// Use QueryTimestamp and ResponseTimestamp to supply both instants; do not combine Timestamp with its matching explicit instant.
        /// </summary>
        public DateTimeOffset? Timestamp { get; init; }

        /// <summary>Explicit query time, independently settable for both query and response events.</summary>
        public DateTimeOffset? QueryTimestamp { get; init; }

        /// <summary>Explicit response time, independently settable for both query and response events.</summary>
        public DateTimeOffset? ResponseTimestamp { get; init; }

        /// <summary>Optional uncompressed, root-terminated DNS wire-format query zone; no text parsing.</summary>
        public ReadOnlyMemory<byte> QueryZone { get; init; }

        /// <summary>Original wire-format DNS query packet, if available.</summary>
        public ReadOnlyMemory<byte> QueryPacket { get; init; }

        /// <summary>Original wire-format DNS response packet, if available.</summary>
        public ReadOnlyMemory<byte> ResponsePacket { get; init; }
    }
}
