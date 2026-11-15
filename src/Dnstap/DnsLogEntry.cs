namespace Dnstap;

/// <summary>
/// String-valued DNS log metadata used to build a dnstap observation.
/// ClientAddress/ClientPort describe the DNS message initiator (dnstap query_*),
/// and ServerAddress/ServerPort describe its responder (dnstap response_*).
/// These roles are not necessarily the machine running this library.
/// </summary>
public sealed record DnsLogEntry
{
    /// <summary>Official dnstap event type, for example CLIENT_QUERY or RESOLVER_RESPONSE.</summary>
    public required string EventType { get; init; }

    public string? ClientAddress { get; init; }
    public string? ServerAddress { get; init; }
    public string? ClientPort { get; init; }
    public string? ServerPort { get; init; }

    /// <summary>Official socket protocol name: UDP, TCP, DOT, DOH, DOQ, DNSCryptUDP or DNSCryptTCP.</summary>
    public string? Transport { get; init; }

    /// <summary>
    /// Time of this observed event. A query event maps to query_time_*;
    /// a response event maps to response_time_*. Other timestamps are not inferred.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Original wire-format DNS query packet, if available. Never reconstructed from text.</summary>
    public ReadOnlyMemory<byte> QueryPacket { get; init; }

    /// <summary>Original wire-format DNS response packet, if available. Never reconstructed from text.</summary>
    public ReadOnlyMemory<byte> ResponsePacket { get; init; }
}
